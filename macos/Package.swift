// swift-tools-version: 5.10
import PackageDescription

let package = Package(
    name: "NotiLinkMac",
    platforms: [.macOS(.v13)],
    products: [
        .library(name: "NotiLinkCore", targets: ["NotiLinkCore"]),
        .executable(name: "NotiLinkMac", targets: ["NotiLinkMac"]),
    ],
    targets: [
        .target(name: "NotiLinkCore"),
        .executableTarget(
            name: "NotiLinkMac",
            dependencies: ["NotiLinkCore"],
            // tools/scrcpy/ is vendored binaries (fetch-scrcpy.sh), not Swift sources — SwiftPM
            // would otherwise complain about "unhandled resources" scanning the target tree.
            exclude: ["tools/scrcpy"]
        ),
        .testTarget(
            name: "NotiLinkCoreTests",
            dependencies: ["NotiLinkCore"]
        ),
        // Temporary, CLT-only verification target (no XCTest/swift-testing macro plugin available
        // in this environment). Remove once `swift test` can run for real (needs full Xcode).
        .executableTarget(
            name: "DevSmokeTest",
            dependencies: ["NotiLinkCore"],
            path: "Sources/DevSmokeTest"
        ),
    ]
)
