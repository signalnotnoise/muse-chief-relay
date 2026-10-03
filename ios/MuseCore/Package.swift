// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "MuseCore",
    platforms: [
        .iOS(.v17),
        .macOS(.v14),
        .visionOS(.v1)
    ],
    products: [
        .library(name: "MuseCore", targets: ["MuseCore"])
    ],
    targets: [
        .target(name: "MuseCore"),
        .testTarget(
            name: "MuseCoreTests",
            dependencies: ["MuseCore"],
            exclude: ["Fixtures"]
        )
    ]
)
