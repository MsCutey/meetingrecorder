import Foundation
import CoreFoundation

/// CFPreferences is public; WindowManager's key is undocumented and must be
/// checked on each supported macOS version. Never restart desktop processes.
enum StageManagerSystemPreferences {
    private static let domain = "com.apple.WindowManager" as CFString
    private static let key = "GloballyEnabled" as CFString

    static func readEnabled() throws -> Bool {
        guard CFPreferencesSynchronize(domain, kCFPreferencesCurrentUser, kCFPreferencesAnyHost),
              let value = CFPreferencesCopyValue(key, domain, kCFPreferencesCurrentUser, kCFPreferencesAnyHost),
              let number = value as? NSNumber,
              number == 0 || number == 1 else {
            throw PreferenceError("无法确认台前调度状态。请关闭自动切换选项，并在系统设置中手动调整后重试。")
        }
        return number.boolValue
    }

    static func writeEnabled(_ enabled: Bool) throws {
        guard !CFPreferencesAppValueIsForced(key, domain) else {
            throw PreferenceError("台前调度由系统管理策略控制，无法自动切换。")
        }
        CFPreferencesSetValue(key, enabled ? kCFBooleanTrue : kCFBooleanFalse,
                              domain, kCFPreferencesCurrentUser, kCFPreferencesAnyHost)
        guard CFPreferencesSynchronize(domain, kCFPreferencesCurrentUser, kCFPreferencesAnyHost),
              try readEnabled() == enabled else {
            throw PreferenceError("无法确认台前调度切换成功，请检查系统设置。")
        }
    }

    private struct PreferenceError: LocalizedError {
        let message: String
        init(_ message: String) { self.message = message }
        var errorDescription: String? { message }
    }
}
