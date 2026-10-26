// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "MeetingRecorderPure",
    platforms: [.macOS(.v15)],
    products: [.executable(name: "MeetingRecorderPure", targets: ["MeetingRecorder"])],
    targets: [
        .target(name: "RecorderSupport"),
        .executableTarget(name: "MeetingRecorder", dependencies: ["RecorderSupport"]),
        .testTarget(name: "RecorderSupportTests", dependencies: ["RecorderSupport"]),
        .testTarget(name: "MeetingRecorderTests", dependencies: ["MeetingRecorder", "RecorderSupport"])
    ],
    swiftLanguageModes: [.v5]
)
