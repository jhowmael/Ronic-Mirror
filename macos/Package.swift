// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "RonicMirror",
    platforms: [.macOS(.v13)],
    targets: [
        // Declarações Objective-C da API privada CGVirtualDisplay (exportada pelo CoreGraphics).
        .target(
            name: "CGVirtualDisplayBridge",
            path: "Sources/CGVirtualDisplayBridge",
            linkerSettings: [.linkedFramework("CoreGraphics")]
        ),
        .executableTarget(
            name: "RonicMirror",
            dependencies: ["CGVirtualDisplayBridge"],
            path: "Sources/RonicMirror",
            linkerSettings: [
                .linkedFramework("ScreenCaptureKit"),
                .linkedFramework("CoreImage"),
                .linkedFramework("AppKit"),
            ]
        ),
    ]
)
