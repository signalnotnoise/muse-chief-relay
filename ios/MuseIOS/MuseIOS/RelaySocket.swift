import Foundation

/// One foreground WebSocket. A newer `connect` retires the previous task so a late close cannot reopen it.
final class RelaySocket: NSObject, URLSessionWebSocketDelegate {
    var onOpen: ((Int) -> Void)?
    var onText: ((String, Int) -> Void)?
    var onClose: ((Int) -> Void)?
    var onSendResult: ((String, Bool) -> Void)?

    private var session: URLSession!
    private var task: URLSessionWebSocketTask?
    private var generation = 0
    private var listening = false

    override init() {
        super.init()
        session = URLSession(configuration: .default, delegate: self, delegateQueue: .main)
    }

    func connect(url: URL, generation: Int) {
        close()
        self.generation = generation
        listening = false
        let task = session.webSocketTask(with: url)
        self.task = task
        task.resume()
    }

    func send(text: String, localID: String) {
        guard let task else {
            onSendResult?(localID, false)
            return
        }
        task.send(.string(text)) { [weak self] error in
            DispatchQueue.main.async {
                self?.onSendResult?(localID, error == nil)
            }
        }
    }

    func close() {
        listening = false
        task?.cancel(with: .goingAway, reason: nil)
        task = nil
    }

    func urlSession(
        _ session: URLSession,
        webSocketTask: URLSessionWebSocketTask,
        didOpenWithProtocol protocol: String?
    ) {
        guard webSocketTask === task else { return }
        onOpen?(generation)
        listen()
    }

    func urlSession(
        _ session: URLSession,
        webSocketTask: URLSessionWebSocketTask,
        didCloseWith closeCode: URLSessionWebSocketTask.CloseCode,
        reason: Data?
    ) {
        guard webSocketTask === task else { return }
        task = nil
        onClose?(generation)
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard let socket = task as? URLSessionWebSocketTask, socket === self.task else { return }
        self.task = nil
        if error != nil {
            onClose?(generation)
        }
    }

    private func listen() {
        guard !listening, let task else { return }
        listening = true
        receive(on: task, generation: generation)
    }

    private func receive(on task: URLSessionWebSocketTask, generation: Int) {
        task.receive { [weak self] result in
            DispatchQueue.main.async {
                guard let self, generation == self.generation, task === self.task else { return }
                switch result {
                case .success(.string(let text)):
                    self.onText?(text, generation)
                    self.receive(on: task, generation: generation)
                case .success(.data(let data)):
                    if let text = String(data: data, encoding: .utf8) {
                        self.onText?(text, generation)
                    }
                    self.receive(on: task, generation: generation)
                case .failure:
                    self.task = nil
                    self.onClose?(generation)
                }
            }
        }
    }
}
