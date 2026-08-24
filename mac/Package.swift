// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "flow",
    platforms: [.macOS("26.0")],
    products: [
        .library(name: "FlowCore", targets: ["FlowCore"]),
        .library(name: "FlowTestSupport", targets: ["FlowTestSupport"]),
        .library(name: "FlowDictation", targets: ["FlowDictation"]),
        .executable(name: "flow", targets: ["FlowCLI"]),
        .executable(name: "FlowApp", targets: ["FlowApp"]),
    ],
    targets: [
        // Shared boundary. Owner: Gil. Consumed by both sides.
        .target(name: "FlowCore"),

        // Fakes so M1 and M2 can be built and tested in isolation. Owner: Gil.
        .target(name: "FlowTestSupport", dependencies: ["FlowCore"]),

        // Speech adapter, formatter, transcript store, injector. Owner: Tony S.
        .target(name: "FlowDictation", dependencies: ["FlowCore"]),

        // Menu-bar agent: permissions, hotkey, capture, HUD, and the M3 integration that wires
        // FlowDictation's transcriber/formatter/injector into the resident loop. Owner: Gil.
        .executableTarget(name: "FlowApp", dependencies: ["FlowCore", "FlowDictation"]),

        // `flow` control CLI. Owner: Gil.
        .executableTarget(name: "FlowCLI", dependencies: ["FlowCore"]),

        .testTarget(
            name: "FlowCoreTests",
            dependencies: ["FlowCore", "FlowTestSupport"]
        ),
        .testTarget(
            name: "FlowDictationTests",
            dependencies: ["FlowDictation", "FlowCore", "FlowTestSupport"]
        ),
    ]
)
