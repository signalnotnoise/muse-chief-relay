import CoreFoundation
import Foundation

enum WireJSON {
    static func object(from text: String) -> [String: Any]? {
        guard let data = text.data(using: .utf8),
              let value = try? JSONSerialization.jsonObject(with: data),
              let object = value as? [String: Any]
        else { return nil }
        return object
    }

    static func stringify(_ object: [String: Any]) -> String? {
        guard JSONSerialization.isValidJSONObject(object),
              let data = try? JSONSerialization.data(withJSONObject: object, options: [.sortedKeys]),
              let text = String(data: data, encoding: .utf8)
        else { return nil }
        return text
    }

    static func bool(_ value: Any?) -> Bool? {
        switch value {
        case let flag as Bool:
            return flag
        case let number as NSNumber:
            if number.isBool { return number.boolValue }
            return nil
        default:
            return nil
        }
    }

    static func int(_ value: Any?) -> Int? {
        switch value {
        case let number as NSNumber where !number.isBool:
            return number.intValue
        case let number as Int:
            return number
        default:
            return nil
        }
    }

    static func int64(_ value: Any?) -> Int64? {
        switch value {
        case let number as NSNumber where !number.isBool:
            return number.int64Value
        case let number as Int64:
            return number
        case let number as Int:
            return Int64(number)
        default:
            return nil
        }
    }

    static func string(_ value: Any?) -> String? {
        value as? String
    }

    static func object(_ value: Any?) -> [String: Any]? {
        value as? [String: Any]
    }

    static func array(_ value: Any?) -> [Any]? {
        value as? [Any]
    }
}

private extension NSNumber {
    var isBool: Bool {
        CFGetTypeID(self) == CFBooleanGetTypeID()
    }
}
