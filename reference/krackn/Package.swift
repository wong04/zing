// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "Krackn",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "Krackn",
            path: "Sources/Krackn"
        )
    ]
)
