import Foundation

/// Result of running an external command.
struct ShellResult {
    let exitCode: Int32
    let stdout: String
    let stderr: String
    var ok: Bool { exitCode == 0 }
}

enum ShellError: LocalizedError {
    case notFound(String)
    case failed(String, String)

    var errorDescription: String? {
        switch self {
        case .notFound(let tool):
            return "\(tool) isn't installed."
        case .failed(let tool, let err):
            return "\(tool) failed: \(err)"
        }
    }
}

/// Pipe readability handlers fire on an arbitrary queue, so the accumulating
/// buffers need to live behind a lock rather than as captured local vars.
private final class OutputBuffers: @unchecked Sendable {
    private let lock = NSLock()
    private var stdout = Data()
    private var stderr = Data()
    private var partialLine = ""

    /// Appends a chunk and, when line splitting is on, returns any complete
    /// lines it produced. Both `\n` and `\r` count as terminators because
    /// ffmpeg and yt-dlp redraw progress with carriage returns.
    func appendStdout(_ chunk: Data, splittingLines: Bool) -> [String] {
        guard !chunk.isEmpty else { return [] }
        lock.lock()
        defer { lock.unlock() }
        stdout.append(chunk)
        guard splittingLines else { return [] }

        partialLine += String(decoding: chunk, as: UTF8.self)
        var lines: [String] = []
        let separators = CharacterSet(charactersIn: "\n\r")
        while let range = partialLine.rangeOfCharacter(from: separators) {
            let line = String(partialLine[partialLine.startIndex..<range.lowerBound])
            partialLine = String(partialLine[range.upperBound...])
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if !trimmed.isEmpty { lines.append(trimmed) }
        }
        return lines
    }

    func appendStderr(_ chunk: Data) {
        guard !chunk.isEmpty else { return }
        lock.lock()
        stderr.append(chunk)
        lock.unlock()
    }

    func result(exitCode: Int32) -> ShellResult {
        lock.lock()
        defer { lock.unlock() }
        return ShellResult(
            exitCode: exitCode,
            stdout: String(decoding: stdout, as: UTF8.self),
            stderr: String(decoding: stderr, as: UTF8.self)
        )
    }
}

/// Runs external binaries. Everything here uses absolute paths or a resolved
/// lookup — we never rely on the app inheriting a useful PATH, because a GUI
/// app launched from Finder gets a very bare one.
enum ShellRunner {

    /// Directories we search for user-installed tools, in priority order.
    /// Homebrew on Apple Silicon lives in /opt/homebrew, Intel in /usr/local.
    ///
    /// The app's own bin directory comes first: tools it downloaded itself take
    /// precedence, so a user with no Homebrew still gets a working ffmpeg.
    static let searchPaths: [String] = {
        let managed = FileManager.default
            .urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Krackn/bin").path
        return [
            managed,
            "/opt/homebrew/bin",
            "/usr/local/bin",
            "/usr/bin",
            "/bin",
            "/usr/sbin",
            // pip --user installs land here.
            NSHomeDirectory() + "/Library/Python/3.9/bin",
            NSHomeDirectory() + "/.local/bin",
        ]
    }()

    static func which(_ tool: String) -> String? {
        if tool.hasPrefix("/") {
            return FileManager.default.isExecutableFile(atPath: tool) ? tool : nil
        }
        for dir in searchPaths {
            let candidate = "\(dir)/\(tool)"
            if FileManager.default.isExecutableFile(atPath: candidate) {
                return candidate
            }
        }
        return nil
    }

    @discardableResult
    static func run(
        _ tool: String,
        _ arguments: [String],
        currentDirectory: URL? = nil,
        onOutputLine: (@Sendable (String) -> Void)? = nil
    ) async throws -> ShellResult {
        guard let executable = which(tool) else { throw ShellError.notFound(tool) }

        return try await withCheckedThrowingContinuation { continuation in
            let process = Process()
            process.executableURL = URL(fileURLWithPath: executable)
            process.arguments = arguments
            if let currentDirectory { process.currentDirectoryURL = currentDirectory }

            var environment = ProcessInfo.processInfo.environment
            environment["PATH"] = searchPaths.joined(separator: ":")
            process.environment = environment

            let outPipe = Pipe()
            let errPipe = Pipe()
            process.standardOutput = outPipe
            process.standardError = errPipe

            let buffers = OutputBuffers()

            // Stream stdout so long-running tools (ffmpeg, yt-dlp) can report progress
            // instead of going silent for minutes.
            outPipe.fileHandleForReading.readabilityHandler = { handle in
                let chunk = handle.availableData
                guard !chunk.isEmpty else { return }
                let lines = buffers.appendStdout(chunk, splittingLines: onOutputLine != nil)
                guard let onOutputLine else { return }
                for line in lines { onOutputLine(line) }
            }

            errPipe.fileHandleForReading.readabilityHandler = { handle in
                let chunk = handle.availableData
                guard !chunk.isEmpty else { return }
                buffers.appendStderr(chunk)
            }

            process.terminationHandler = { proc in
                outPipe.fileHandleForReading.readabilityHandler = nil
                errPipe.fileHandleForReading.readabilityHandler = nil
                // Drain anything buffered between the last read and exit.
                _ = buffers.appendStdout(
                    outPipe.fileHandleForReading.availableData, splittingLines: false)
                buffers.appendStderr(errPipe.fileHandleForReading.availableData)
                continuation.resume(returning: buffers.result(exitCode: proc.terminationStatus))
            }

            do {
                try process.run()
            } catch {
                continuation.resume(throwing: error)
            }
        }
    }
}
