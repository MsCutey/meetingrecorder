import AppKit
import Combine
@preconcurrency import ScreenCaptureKit

/// Owns one system-picker selection through the corresponding recording's lifetime.
/// Call endSelection() after stopping the stream or abandoning its startup.
@MainActor final class WindowCapturePicker: NSObject, ObservableObject {
    @Published private(set) var isSelecting = false
    private var selectionID: UUID?
    private var continuation: CheckedContinuation<SCContentFilter, Error>?
    private var observer: SelectionObserver?
    private var previousConfiguration: SCContentSharingPickerConfiguration?
    private var previousMaximumStreamCount: Int?

    func selectWindow() async throws -> SCContentFilter {
        try Task.checkCancellation()
        let picker = SCContentSharingPicker.shared
        guard selectionID == nil, !picker.isActive else {
            throw RecorderError("窗口选择器正在使用，请先结束当前选择或录制。")
        }
        let id = UUID()
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { waiting in
                guard !Task.isCancelled else {
                    waiting.resume(throwing: CancellationError())
                    return
                }
                selectionID = id
                continuation = waiting
                isSelecting = true
                previousConfiguration = picker.defaultConfiguration
                previousMaximumStreamCount = picker.maximumStreamCount
                var configuration = SCContentSharingPickerConfiguration()
                configuration.allowedPickerModes = [.singleWindow]
                configuration.allowsChangingSelectedContent = false
                picker.defaultConfiguration = configuration
                picker.maximumStreamCount = 1
                let observer = SelectionObserver(owner: self, selectionID: id)
                self.observer = observer
                picker.add(observer)
                picker.isActive = true
                picker.present(using: .window)
            }
        } onCancel: {
            Task { @MainActor [weak self] in
                self?.cancelSelection(id: id, error: CancellationError())
            }
        }
    }

    /// Cancels a pending choice and releases only this object's app-local picker session.
    func cancel() {
        guard let id = selectionID else { return }
        cancelSelection(id: id, error: CancellationError())
    }

    func endSelection() { cancel() }

    private func selected(_ filter: SCContentFilter, id: UUID) {
        guard selectionID == id, let waiting = continuation else { return }
        continuation = nil
        isSelecting = false
        // Keep the picker active while the returned filter is used by its SCStream.
        waiting.resume(returning: filter)
    }

    private func pickerFailed(_ error: Error, id: UUID) {
        guard continuation != nil else { return }
        cancelSelection(id: id, error: error)
    }

    private func cancelSelection(id: UUID, error: Error) {
        guard selectionID == id else { return }
        let waiting = continuation
        continuation = nil
        selectionID = nil
        isSelecting = false
        let picker = SCContentSharingPicker.shared
        if let observer { picker.remove(observer) }
        observer = nil
        picker.isActive = false
        if let previousConfiguration { picker.defaultConfiguration = previousConfiguration }
        picker.maximumStreamCount = previousMaximumStreamCount
        previousConfiguration = nil
        previousMaximumStreamCount = nil
        waiting?.resume(throwing: error)
    }

    /// A new observer per selection makes callbacks from an old cancelled UI harmless.
    @MainActor private final class SelectionObserver: NSObject, SCContentSharingPickerObserver {
        weak var owner: WindowCapturePicker?
        let selectionID: UUID

        init(owner: WindowCapturePicker, selectionID: UUID) {
            self.owner = owner
            self.selectionID = selectionID
        }

        nonisolated func contentSharingPicker(_ picker: SCContentSharingPicker, didCancelFor stream: SCStream?) {
            guard stream == nil else { return }
            Task { @MainActor [weak self] in
                guard let self else { return }
                owner?.pickerFailed(CancellationError(), id: selectionID)
            }
        }

        nonisolated func contentSharingPicker(_ picker: SCContentSharingPicker,
                                              didUpdateWith filter: SCContentFilter, for stream: SCStream?) {
            guard stream == nil else { return }
            Task { @MainActor [weak self] in
                guard let self else { return }
                owner?.selected(filter, id: selectionID)
            }
        }

        nonisolated func contentSharingPickerStartDidFailWithError(_ error: Error) {
            Task { @MainActor [weak self] in
                guard let self else { return }
                owner?.pickerFailed(error, id: selectionID)
            }
        }
    }
}
