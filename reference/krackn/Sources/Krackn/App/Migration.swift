import Foundation
import Security

/// Carries settings and the saved account across the rename from "All in One"
/// to Krackn.
///
/// Both preferences and Keychain items are keyed by bundle identifier, so
/// changing it would otherwise orphan everything the user had set up — theme,
/// search engine, dock state, and the sign-in itself. This runs once and then
/// records that it's done.
enum Migration {
    private static let legacyBundleID = "com.allinone.app"
    /// The service name the *old* app wrote under. This was previously aliased
    /// to `keychainService` — the new name — which made `migrateKeychain()`
    /// dead code: it bailed out when the new item existed, then read from that
    /// same service, so it could never find anything to carry over. Every
    /// returning user landed on the "set your password again" screen.
    private static let legacyKeychainService = "com.allinone.app.account"
    static let keychainService = "com.krackn.app.account"

    private static let completedKey = "aio.migratedFromAllInOne"

    /// Called once at launch, before any view reads a preference.
    static func runIfNeeded() {
        let defaults = UserDefaults.standard
        guard !defaults.bool(forKey: completedKey) else { return }

        migratePreferences()
        migrateKeychain()
        migrateSupportFolder()

        defaults.set(true, forKey: completedKey)
    }

    /// Copies every key from the old preferences domain that we don't already
    /// have. Existing values win, so re-running can never clobber newer state.
    private static func migratePreferences() {
        guard let legacy = UserDefaults(suiteName: legacyBundleID) else { return }
        let target = UserDefaults.standard
        for (key, value) in legacy.dictionaryRepresentation() {
            // Skip Apple's own global keys, which appear in every domain.
            guard !key.hasPrefix("Apple"), !key.hasPrefix("NS"), !key.hasPrefix("com.apple")
            else { continue }
            if target.object(forKey: key) == nil {
                target.set(value, forKey: key)
            }
        }
    }

    /// Moves the downloaded engines (ffmpeg, yt-dlp, the Python env) to the new
    /// support folder. Without this they'd still be on disk but invisible, and
    /// the user would be told to download them all over again.
    private static func migrateSupportFolder() {
        let fm = FileManager.default
        guard let support = fm.urls(for: .applicationSupportDirectory,
                                    in: .userDomainMask).first else { return }
        let old = support.appendingPathComponent("AllInOne", isDirectory: true)
        let new = support.appendingPathComponent("Krackn", isDirectory: true)

        guard fm.fileExists(atPath: old.path) else { return }
        if fm.fileExists(atPath: new.path) {
            // Both exist: keep the new one and leave the old alone rather than
            // risk merging two trees.
            return
        }
        try? fm.moveItem(at: old, to: new)
    }

    /// Re-files the account's password verifier under the new service name.
    /// Without this, a returning user's password would stop matching.
    private static func migrateKeychain() {
        // Nothing to do if the new item already exists.
        let newQuery: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: keychainService,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        if SecItemCopyMatching(newQuery as CFDictionary, nil) == errSecSuccess { return }

        // Read whatever the old service held, keeping its account name.
        let readQuery: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: legacyKeychainService,
            kSecReturnData as String: true,
            kSecReturnAttributes as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var item: CFTypeRef?
        guard SecItemCopyMatching(readQuery as CFDictionary, &item) == errSecSuccess,
              let existing = item as? [String: Any],
              let data = existing[kSecValueData as String] as? Data,
              let account = existing[kSecAttrAccount as String] as? String
        else { return }

        let insert: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: keychainService,
            kSecAttrAccount as String: account,
            kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleWhenUnlockedThisDeviceOnly,
        ]
        SecItemAdd(insert as CFDictionary, nil)
    }
}
