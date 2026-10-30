import Foundation
import CryptoKit
import Security

/// A local account. Everything lives on this Mac: the profile in preferences,
/// the password verifier in the login Keychain.
///
/// This is deliberately **not** a cloud account. There's no server behind the
/// app, so there's nothing to sync to and no credentials leave the machine.
/// What it does give you is a persistent identity the app remembers — your
/// name, preferences, and per-user history — plus an optional lock.
struct Account: Codable, Equatable {
    var username: String
    var displayName: String
    var email: String
    var createdAt: Date
    /// Random per-account salt, so two people with the same password produce
    /// different verifiers.
    var salt: Data
}

@MainActor
final class AccountStore: ObservableObject {
    static let shared = AccountStore()

    @Published private(set) var account: Account?
    @Published private(set) var isSignedIn = false
    @Published var lastError: String?

    private let accountKey = "aio.account"
    private let keychainService = Migration.keychainService

    private init() {
        load()
        // A returning user is signed in automatically unless they asked for a
        // lock — "remember me" is the default, which is the whole point.
        if account != nil && !requiresPasswordOnLaunch && !needsPasswordReset {
            isSignedIn = true
        }
    }

    /// True when a profile exists but its password verifier doesn't.
    ///
    /// Keychain items are bound to the code signature of the app that created
    /// them, so the rename to Krackn left the old verifier unreadable. The
    /// profile itself lives in preferences and migrated fine — so rather than
    /// lock the user out of their own local account, we let them set the
    /// password again. This is deliberately narrow: it only applies when the
    /// verifier is *absent*, never when a supplied password is simply wrong.
    var needsPasswordReset: Bool {
        guard let account else { return false }
        return readVerifier(username: account.username) == nil
    }

    /// Sets a password for an account whose verifier went missing.
    func resetPassword(to password: String) throws {
        guard let account else { throw AccountError.noAccount }
        guard needsPasswordReset else { throw AccountError.wrongPassword }
        guard password.count >= 8 else { throw AccountError.weakPassword }
        guard storeVerifier(verifier(for: password, salt: account.salt),
                            username: account.username) else {
            throw AccountError.keychainFailed
        }
        isSignedIn = true
    }

    /// When on, the app asks for the password at every launch.
    var requiresPasswordOnLaunch: Bool {
        get { UserDefaults.standard.bool(forKey: "aio.requirePassword") }
        set { UserDefaults.standard.set(newValue, forKey: "aio.requirePassword") }
    }

    // MARK: - Persistence

    private func load() {
        guard let data = UserDefaults.standard.data(forKey: accountKey),
              let decoded = try? JSONDecoder().decode(Account.self, from: data)
        else { return }
        account = decoded
    }

    private func save(_ account: Account) {
        guard let data = try? JSONEncoder().encode(account) else { return }
        UserDefaults.standard.set(data, forKey: accountKey)
        self.account = account
    }

    // MARK: - Password handling

    /// Stores a *verifier*, never the password itself. Even with the Keychain
    /// item in hand you can't recover the original.
    private func verifier(for password: String, salt: Data) -> Data {
        let key = SymmetricKey(data: salt)
        return Data(HMAC<SHA256>.authenticationCode(for: Data(password.utf8), using: key))
    }

    private func storeVerifier(_ data: Data, username: String) -> Bool {
        // Replace any existing item for this account.
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: keychainService,
            kSecAttrAccount as String: username,
        ]
        SecItemDelete(query as CFDictionary)

        var insert = query
        insert[kSecValueData as String] = data
        // Only readable when the Mac is unlocked, and never synced off-device.
        insert[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
        return SecItemAdd(insert as CFDictionary, nil) == errSecSuccess
    }

    private func readVerifier(username: String) -> Data? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: keychainService,
            kSecAttrAccount as String: username,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess else {
            return nil
        }
        return item as? Data
    }

    // MARK: - Sign up / in

    enum AccountError: LocalizedError {
        case usernameTaken
        case weakPassword
        case emptyField
        case wrongPassword
        case noAccount
        case keychainFailed

        var errorDescription: String? {
            switch self {
            case .usernameTaken: return "An account already exists on this Mac."
            case .weakPassword: return "Use at least 8 characters."
            case .emptyField: return "Fill in every field."
            case .wrongPassword: return "That password doesn't match."
            case .noAccount: return "No account yet — create one first."
            case .keychainFailed: return "Couldn't save to the Keychain."
            }
        }
    }

    func signUp(username: String, displayName: String, email: String,
                password: String) throws {
        guard account == nil else { throw AccountError.usernameTaken }
        let user = username.trimmingCharacters(in: .whitespaces)
        guard !user.isEmpty, !password.isEmpty else { throw AccountError.emptyField }
        guard password.count >= 8 else { throw AccountError.weakPassword }

        var salt = Data(count: 32)
        _ = salt.withUnsafeMutableBytes {
            SecRandomCopyBytes(kSecRandomDefault, 32, $0.baseAddress!)
        }

        guard storeVerifier(verifier(for: password, salt: salt), username: user) else {
            throw AccountError.keychainFailed
        }

        save(Account(
            username: user,
            displayName: displayName.isEmpty ? user : displayName,
            email: email,
            createdAt: Date(),
            salt: salt))
        isSignedIn = true
    }

    func signIn(password: String) throws {
        guard let account else { throw AccountError.noAccount }
        guard let stored = readVerifier(username: account.username) else {
            throw AccountError.noAccount
        }
        let candidate = verifier(for: password, salt: account.salt)
        // Constant-time comparison, so timing can't leak how much matched.
        guard candidate.count == stored.count,
              zip(candidate, stored).reduce(0, { $0 | ($1.0 ^ $1.1) }) == 0
        else { throw AccountError.wrongPassword }
        isSignedIn = true
    }

    func signOut() { isSignedIn = false }

    func updateProfile(displayName: String, email: String) {
        guard var account else { return }
        account.displayName = displayName
        account.email = email
        save(account)
    }

    /// Removes the account and its Keychain entry. Preferences and downloaded
    /// engines are left alone — this only forgets who you are.
    func deleteAccount() {
        if let username = account?.username {
            SecItemDelete([
                kSecClass as String: kSecClassGenericPassword,
                kSecAttrService as String: keychainService,
                kSecAttrAccount as String: username,
            ] as CFDictionary)
        }
        UserDefaults.standard.removeObject(forKey: accountKey)
        account = nil
        isSignedIn = false
    }

    /// Lets someone use the app without an account. They can create one later
    /// from Settings.
    func continueAsGuest() { isSignedIn = true }
}
