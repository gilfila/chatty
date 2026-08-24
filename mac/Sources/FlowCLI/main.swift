import Foundation
import FlowCore

// `flow` control CLI. Owner: Gil (M0 contract / M1 implementation).
//
// The CLI never owns dictation state — it starts, stops, inspects and configures the resident
// FlowApp. Every command exits non-zero on failure and prints machine-readable JSON under
// `--json` so `flow diagnose` can be pasted into a bug report.

let usage = """
flow — control the Flow dictation agent

USAGE:
  flow <command> [--json]

COMMANDS:
  install     Register the app bundle and offer launch-at-login
  start       Launch the resident agent (no-op if already running)
  stop        Terminate the resident agent
  status      One-line state: running, permissions, current locale
  diagnose    Full report: version, signing, permissions, backend, launch-at-login
  open        Bring the agent's settings/history window forward

EXIT CODES:
  0 ok   1 usage   2 agent not running   3 permission missing   4 internal
"""

enum ExitCode: Int32 {
    case ok = 0, usage = 1, notRunning = 2, permission = 3, internalError = 4
}

func fail(_ message: String, _ code: ExitCode) -> Never {
    FileHandle.standardError.write(Data((message + "\n").utf8))
    exit(code.rawValue)
}

let arguments = Array(CommandLine.arguments.dropFirst())
guard let command = arguments.first else {
    print(usage)
    exit(ExitCode.usage.rawValue)
}
let wantsJSON = arguments.contains("--json")

switch command {
case "-h", "--help", "help":
    print(usage)

case "install", "start", "stop", "open":
    // M1 wires these to SMAppService + NSWorkspace against the real bundle.
    fail("`flow \(command)` lands in M1 with the signed app bundle.", .internalError)

case "status", "diagnose":
    // Contract shape is fixed now so Tony S and Jarvis can write against it; the values become
    // real in M1 once the permission coordinator exists.
    if wantsJSON {
        let report: [String: Any] = [
            "command": command,
            "implemented": false,
            "milestone": "M1",
            "permissions": PermissionKind.allCases.reduce(into: [String: String]()) {
                $0[$1.rawValue] = PermissionStatus.notDetermined.rawValue
            },
        ]
        let data = try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys])
        print(String(decoding: data, as: UTF8.self))
    } else {
        fail("`flow \(command)` lands in M1 with the permission coordinator.", .internalError)
    }

default:
    fail("unknown command: \(command)\n\n\(usage)", .usage)
}
