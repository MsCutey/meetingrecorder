// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "MeetingRecorder",
    platforms: [.macOS(.v15)],
    products: [.executable(name: "MeetingRecorder", targets: ["MeetingRecorder"])],
    targets: [
        .target(name: "RecorderSupport"),
        .executableTarget(name: "MeetingRecorder", dependencies: ["RecorderSupport"]),
        .testTarget(name: "RecorderSupportTests", dependencies: ["RecorderSupport"])
    ],
    swiftLanguageModes: [.v5]
)
