// Port of Krackn's `DownloadPolicy` (Features/Browser/DownloadPolicy.swift).
//
// Krackn's web view only downloaded when the app answered "download" for a
// response; without that answer it tried to display everything, and a .zip or
// an installer it couldn't display failed silently.

export interface DownloadResponse {
	/** Whether the engine can display the response's MIME type. */
	canShow: boolean
	/** The response's `Content-Disposition` header, if any. */
	contentDisposition: string | null
	/** Whether this is the page itself, not an embedded frame. */
	isMainFrame: boolean
}

/** Whether a response should be saved as a file rather than shown. */
export function shouldDownload({ canShow, contentDisposition, isMainFrame }: DownloadResponse): boolean {
	// The server saying "this is a file" — how most download buttons work,
	// often from a hidden frame, so frames count here too.
	if (contentDisposition?.trim().toLowerCase().startsWith('attachment')) return true
	// Something the browser can't display, opened as a page: the only
	// useful thing to do with it is save it. Not for embedded frames,
	// where an odd content type from an ad shouldn't drop a file in
	// Downloads.
	return isMainFrame && !canShow
}
