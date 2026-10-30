import SwiftUI

/// Sign-in / sign-up gate shown before the main window when there's no active
/// session. Deliberately calm: one card, centred, no chrome.
struct AuthView: View {
    @ObservedObject private var store = AccountStore.shared

    @State private var mode: Mode = AccountStore.shared.account == nil ? .signUp : .signIn
    @State private var username = ""
    @State private var displayName = ""
    @State private var email = ""
    @State private var password = ""
    @State private var confirm = ""
    @State private var error: String?

    enum Mode { case signIn, signUp }

    var body: some View {
        ZStack {
            Palette.background.ignoresSafeArea()

            VStack(spacing: 0) {
                // Only this strip drags the window, matching the rest of the app.
                WindowDragArea().frame(height: 30)

                Spacer()

                VStack(spacing: 20) {
                    header

                    VStack(spacing: 11) {
                        if mode == .signUp {
                            LabeledField(label: "Username", text: $username,
                                         icon: "person", placeholder: "jdoe")
                            LabeledField(label: "Display name", text: $displayName,
                                         icon: "textformat", placeholder: "Jane Doe")
                            LabeledField(label: "Email (optional)", text: $email,
                                         icon: "envelope", placeholder: "jane@example.com")
                            SecureLabeledField(label: "Password", text: $password,
                                               placeholder: "at least 8 characters")
                            SecureLabeledField(label: "Confirm", text: $confirm,
                                               placeholder: "repeat it")
                        } else {
                            if let account = store.account {
                                HStack(spacing: 10) {
                                    Circle().fill(Palette.accent)
                                        .frame(width: 34, height: 34)
                                        .overlay(
                                            Text(initials(account.displayName))
                                                .font(.app(13, .semibold))
                                                .foregroundStyle(Palette.onAccent))
                                        .overlay(Circle().strokeBorder(
                                            Palette.separator, lineWidth: 1))
                                    VStack(alignment: .leading, spacing: 1) {
                                        Text(account.displayName)
                                            .font(.app(14, .semibold))
                                            .foregroundStyle(Palette.text)
                                        Text("@\(account.username)")
                                            .font(.app(11))
                                            .foregroundStyle(Palette.textSecondary)
                                    }
                                    Spacer()
                                }
                                .padding(.bottom, 4)
                            }
                            SecureLabeledField(label: "Password", text: $password,
                                               placeholder: "your password")
                        }
                    }

                    if store.needsPasswordReset && mode == .signIn {
                        InfoBanner(
                            kind: .info,
                            text: "**Set your password again.** The app was renamed to Krackn, "
                                + "and macOS ties saved passwords to the app that created them — "
                                + "so the old one can't be read. Your profile and settings are "
                                + "intact; just choose a password.")
                    }

                    if let error {
                        InfoBanner(kind: .warning, text: error)
                    }

                    VStack(spacing: 9) {
                        PillButton(mode == .signUp ? "Create Account"
                                     : (store.needsPasswordReset ? "Set Password" : "Sign In"),
                                   icon: "arrow.right", style: .filled, size: .large) {
                            submit()
                        }
                        .frame(maxWidth: .infinity)

                        HStack(spacing: 14) {
                            if store.account != nil {
                                PillButton(mode == .signUp ? "I have an account" : "Create new",
                                           style: .ghost, size: .small) {
                                    mode = mode == .signUp ? .signIn : .signUp
                                    error = nil
                                }
                            }
                            PillButton("Skip for now", style: .ghost, size: .small) {
                                store.continueAsGuest()
                            }
                        }
                    }

                    Text("Your account stays on this Mac. The password is stored as a "
                         + "one-way verifier in your Keychain — there's no server, so "
                         + "nothing is uploaded anywhere.")
                        .font(.app(10.5))
                        .foregroundStyle(Palette.textSecondary)
                        .multilineTextAlignment(.center)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .padding(30)
                .frame(width: 400)
                .cardSurface()

                Spacer()
            }
        }
        .fontDesign(.default)
    }

    private var header: some View {
        VStack(spacing: 8) {
            KracknLogo(size: 52)
            Text(mode == .signUp ? "Create your account" : "Welcome back")
                .font(.app(22, .semibold))
                .foregroundStyle(Palette.text)
            Text(mode == .signUp
                 ? "So the app remembers your setup and preferences."
                 : (store.needsPasswordReset
                    ? "Choose a password to finish moving your account over."
                    : "Sign in to pick up where you left off."))
                .font(.app(12.5))
                .foregroundStyle(Palette.textSecondary)
                .multilineTextAlignment(.center)
        }
    }

    private func initials(_ name: String) -> String {
        let parts = name.split(separator: " ")
        let letters = parts.prefix(2).compactMap { $0.first }
        return letters.isEmpty ? "?" : String(letters).uppercased()
    }

    private func submit() {
        error = nil
        do {
            if mode == .signUp {
                guard password == confirm else {
                    error = "The two passwords don't match."
                    return
                }
                try store.signUp(username: username, displayName: displayName,
                                 email: email, password: password)
            } else if store.needsPasswordReset {
                try store.resetPassword(to: password)
            } else {
                try store.signIn(password: password)
            }
        } catch {
            self.error = error.localizedDescription
        }
    }
}

// MARK: - Fields

private struct LabeledField: View {
    let label: String
    @Binding var text: String
    let icon: String
    let placeholder: String

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(label)
                .font(.app(10.5, .semibold))
                .foregroundStyle(Palette.textSecondary)
            PillField(placeholder: placeholder, text: $text, icon: icon)
        }
    }
}

private struct SecureLabeledField: View {
    let label: String
    @Binding var text: String
    let placeholder: String
    @FocusState private var focused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(label)
                .font(.app(10.5, .semibold))
                .foregroundStyle(Palette.textSecondary)
            HStack(spacing: 8) {
                Image(systemName: "lock")
                    .font(.app(12))
                    .foregroundStyle(Palette.textSecondary)
                SecureField(placeholder, text: $text)
                    .textFieldStyle(.plain)
                    .font(.app(13))
                    .foregroundStyle(Palette.text)
                    .focused($focused)
            }
            .padding(.vertical, 9)
            .padding(.horizontal, 14)
            .background(Palette.well, in: Capsule())
            .overlay(Capsule().strokeBorder(
                focused ? Palette.accent : Palette.separator, lineWidth: 1))
        }
    }
}
