import Foundation

/// Interpreting yt-dlp's failures.
///
/// Pure and view-free on purpose: the classification decides whether the app
/// silently retries, offers an update, or just reports — so it's the part worth
/// testing, and it shouldn't drag a SwiftUI view into the test suite to do it.
enum DownloadErrors {

    /// Failures that mean "the extractor is out of date", as opposed to a bad
    /// URL or a private video. These are the ones a newer yt-dlp fixes.
    ///
    /// The distinction matters: offering "update yt-dlp" for a private video
    /// teaches people to ignore the suggestion, and then it's useless on the
    /// day it would have worked.
    static func looksLikeExtractorBreakage(_ stderr: String) -> Bool {
        let text = stderr.lowercased()
        // Checked first — these are ordinary refusals no update can fix, and
        // several of them mention a status code that would otherwise match.
        let benign = [
            "private video", "video unavailable", "members-only",
            "is not a valid url", "unsupported url", "no space left",
            "sign in to confirm your age",
        ]
        if benign.contains(where: { text.contains($0) }) { return false }

        let signals = [
            "403", "forbidden",
            "unable to download video data",
            "unable to extract",
            "nsig extraction failed",
            "signature extraction failed",
            "unable to extract player response",
            "requested format is not available",
        ]
        return signals.contains { text.contains($0) }
    }

    /// Turns yt-dlp's last line into something a person can act on.
    static func explain(_ stderr: String, exitCode: Int32) -> String {
        let text = stderr.lowercased()
        if text.contains("private video") { return "This video is private." }
        if text.contains("video unavailable") { return "This video isn't available." }
        if text.contains("sign in to confirm your age") || text.contains("age-restricted") {
            return "This video is age-restricted, so it can't be downloaded without "
                + "signing in."
        }
        if text.contains("no space left") { return "The disk is full." }
        if text.contains("is not a valid url") || text.contains("unsupported url") {
            return "That link isn't one yt-dlp recognises."
        }
        if text.contains("403") || text.contains("forbidden") {
            return "YouTube refused the download (HTTP 403). This nearly always means "
                + "yt-dlp needs updating — YouTube changes how it signs media links every "
                + "few weeks, and an older copy stops being able to follow them."
        }
        if text.contains("ffmpeg") {
            return "This needs FFmpeg to finish. Install it from Convert → Set Up Tools."
        }
        return stderr.split(separator: "\n").last.map(String.init)
            ?? "yt-dlp exited with code \(exitCode)."
    }
}
