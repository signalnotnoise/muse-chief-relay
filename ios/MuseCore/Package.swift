// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "MuseCore",
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
