using T9Pane.Native;
using T9Pane.Services;

namespace T9Pane.Tests;

public class KeyboardPlacerTests
{
    // 固定落点策略：键盘钉在输入框所在显示器工作区的右下角，与光标/输入框位置无关。
    // Gap=8；尺寸夹在 [160, work-16] 之间。这些用例验证该落点而不是旧的跟随光标行为。

    [Fact]
    public void Flyout_search_box_anchors_keyboard_to_work_bottom_right()
    {
        var fly = Rect(400, 200, 1200, 900);
        var search = Rect(420, 840, 980, 888);
        var work = Rect(0, 0, 1920, 1080);
        var box = KeyboardPlacer.Place(new InputField(IntPtr.Zero, search, fly), 620, 360, work);

        AssertBottomRight(box, work, 620, 360);
    }

    [Fact]
    public void Ordinary_field_ignores_caret_and_anchors_bottom_right()
    {
        var caret = Rect(200, 400, 202, 424);
        var work = Rect(0, 0, 1920, 1080);
        var box = KeyboardPlacer.Place(new InputField(IntPtr.Zero, caret, default), 620, 360, work);

        Assert.False(box.IsEmpty);
        AssertBottomRight(box, work, 620, 360);
    }

    [Fact]
    public void Keyboard_that_fits_keeps_requested_size_in_bottom_right()
    {
        var caret = Rect(200, 480, 202, 506);
        var work = Rect(0, 0, 1920, 1040);
        var box = KeyboardPlacer.Place(
            new InputField(IntPtr.Zero, caret, default), 620, 540, work);

        AssertBottomRight(box, work, 620, 540);
    }

    [Fact]
    public void Keyboard_larger_than_work_is_clamped_but_stays_inside()
    {
        var caret = Rect(200, 480, 202, 506);
        var work = Rect(0, 0, 800, 500);
        var box = KeyboardPlacer.Place(
            new InputField(IntPtr.Zero, caret, default), 620, 540, work);

        // 高度 540 超过 work-16=484 被夹住；宽度 620 未超限保持原值。整体仍在工作区内。
        Assert.True(box.Left >= work.Left + 8);
        Assert.True(box.Top >= work.Top + 8);
        Assert.True(box.Right <= work.Right - 8);
        Assert.True(box.Bottom <= work.Bottom - 8);
        Assert.Equal(620, box.Width);
        Assert.Equal(work.Height - 16, box.Height);
    }

    [Fact]
    public void Tall_chat_compose_box_still_anchors_bottom_right()
    {
        var caret = Rect(480, 1140, 482, 1170);
        var fieldBox = Rect(400, 1080, 980, 1190);
        var work = Rect(0, 0, 1920, 1200);
        var box = KeyboardPlacer.Place(
            new InputField(
                IntPtr.Zero,
                caret,
                default,
                FieldBox: fieldBox),
            620,
            360,
            work);

        AssertBottomRight(box, work, 620, 360);
    }

    [Fact]
    public void Untrusted_caret_on_tall_field_still_anchors_bottom_right()
    {
        var boxTop = Rect(1058, 202, 1060, 226);
        var fieldBox = Rect(400, 200, 1200, 1180);
        var work = Rect(0, 0, 1920, 1200);
        var placed = KeyboardPlacer.Place(
            new InputField(
                IntPtr.Zero,
                boxTop,
                default,
                CaretIsTrusted: false,
                FieldBox: fieldBox),
            620,
            360,
            work);

        AssertBottomRight(placed, work, 620, 360);
    }

    [Fact]
    public void Compact_field_box_anchors_bottom_right()
    {
        var caret = Rect(420, 844, 422, 868);
        var fieldBox = Rect(400, 840, 980, 888);
        var work = Rect(0, 0, 1920, 1080);
        var box = KeyboardPlacer.Place(
            new InputField(
                IntPtr.Zero,
                caret,
                default,
                FieldBox: fieldBox),
            620,
            360,
            work);

        AssertBottomRight(box, work, 620, 360);
    }

    private static void AssertBottomRight(NativeRect box, NativeRect work, int width, int height)
    {
        const int gap = 8;
        width = Math.Min(width, Math.Max(160, work.Width - gap * 2));
        height = Math.Min(height, Math.Max(160, work.Height - gap * 2));
        Assert.Equal(width, box.Width);
        Assert.Equal(height, box.Height);
        Assert.Equal(work.Right - width - gap, box.Left);
        Assert.Equal(work.Bottom - height - gap, box.Top);
        Assert.True(box.Right <= work.Right - gap);
        Assert.True(box.Bottom <= work.Bottom - gap);
    }

    [Fact]
    public void Changing_application_restarts_visible_position_session()
    {
        Assert.True(KeyboardPositionSession.ShouldRestart(
            visible: true,
            currentHost: new IntPtr(100),
            nextHost: new IntPtr(200)));
    }

    [Fact]
    public void Same_application_keeps_dragged_position_until_hidden()
    {
        Assert.False(KeyboardPositionSession.ShouldRestart(
            visible: true,
            currentHost: new IntPtr(100),
            nextHost: new IntPtr(100)));
        Assert.False(KeyboardPositionSession.ShouldRestart(
            visible: false,
            currentHost: new IntPtr(100),
            nextHost: new IntPtr(200)));
        Assert.False(KeyboardPositionSession.ShouldTearDownBeforePlace(
            restart: true,
            nextRequiresHostRender: true));
        Assert.True(KeyboardPositionSession.ShouldTearDownBeforePlace(
            restart: true,
            nextRequiresHostRender: false));
        Assert.False(KeyboardPositionSession.ShouldTearDownBeforePlace(
            restart: false,
            nextRequiresHostRender: false));
    }

    [Fact]
    public void Moved_position_is_kept_only_while_caret_anchor_is_unchanged()
    {
        var first = Rect(200, 100, 820, 460);
        var sameCaret = Rect(206, 106, 826, 466);
        var secondCaret = Rect(600, 420, 1220, 780);

        Assert.True(KeyboardPositionSession.ShouldKeepMovedPosition(
            movedByUser: true,
            previousAnchor: first,
            nextAnchor: sameCaret));
        Assert.False(KeyboardPositionSession.ShouldKeepMovedPosition(
            movedByUser: true,
            previousAnchor: first,
            nextAnchor: secondCaret));
    }

    [Fact]
    public void System_search_rejects_native_caret_from_different_surface()
    {
        var ui = new InputField(new IntPtr(10), Rect(370, 8, 372, 40), default);
        var native = new InputField(new IntPtr(20), Rect(36, 8, 38, 40), default);

        Assert.True(InputFieldSelectionPolicy.TrySelect(
            systemTextHost: true,
            hasUiField: true,
            ui,
            hasNativeField: true,
            native,
            out var selected));
        Assert.Equal(ui, selected);
        Assert.True(InputFieldSelectionPolicy.NeedsAuthoritativeFirstShow(
            systemTextHost: true,
            hasUiField: true,
            ui,
            hasNativeField: true,
            native));
    }

    [Fact]
    public void Same_row_keeps_uia_field_box_so_address_bar_first_click_belongs()
    {
        var addressBox = Rect(200, 50, 1600, 90);
        var ui = new InputField(
            new IntPtr(10),
            Rect(265, 59, 267, 82),
            default,
            CaretIsTrusted: true,
            FieldId: "explorer.address",
            FieldBox: addressBox);
        var native = new InputField(
            new IntPtr(10),
            Rect(427, 59, 429, 82),
            default);

        Assert.True(InputFieldSelectionPolicy.TrySelect(
            systemTextHost: false,
            hasUiField: true,
            ui,
            hasNativeField: true,
            native,
            out var selected));
        Assert.Equal(native.Caret, selected.Caret);
        Assert.Equal(addressBox, selected.FieldBox);
        Assert.Equal("explorer.address", selected.FieldId);
        Assert.True(FieldClickPolicy.Belongs(selected.FieldBox, selected.Caret, 974, 66));
        Assert.False(FieldClickPolicy.Belongs(default, native.Caret, 974, 66));
    }

    [Fact]
    public void Uwp_uses_authoritative_native_tsf_field_on_same_surface()
    {
        var ui = new InputField(new IntPtr(10), Rect(370, 8, 372, 40), default);
        var native = new InputField(new IntPtr(10), Rect(36, 8, 38, 40), default);

        Assert.True(InputFieldSelectionPolicy.TrySelect(
            systemTextHost: true,
            hasUiField: true,
            ui,
            hasNativeField: true,
            native,
            out var selected));
        Assert.Equal(native, selected);
        Assert.False(InputFieldSelectionPolicy.NeedsAuthoritativeFirstShow(
            systemTextHost: true,
            hasUiField: true,
            ui,
            hasNativeField: true,
            native));
    }

    [Fact]
    public void Search_rejects_stale_native_caret_on_same_window_but_other_row()
    {
        var focusedTopSearch = new InputField(
            new IntPtr(10),
            Rect(60, 40, 62, 92),
            default);
        var staleTaskbarCaret = new InputField(
            new IntPtr(10),
            Rect(100, 1010, 102, 1034),
            default);

        Assert.True(InputFieldSelectionPolicy.NeedsAuthoritativeFirstShow(
            systemTextHost: true,
            hasUiField: true,
            focusedTopSearch,
            hasNativeField: true,
            staleTaskbarCaret));
    }

    [Fact]
    public void Desktop_ui_focus_can_use_more_precise_native_caret()
    {
        var ui = new InputField(new IntPtr(10), Rect(200, 400, 202, 424), default);
        var native = new InputField(new IntPtr(10), Rect(520, 640, 522, 664), default);

        Assert.True(InputFieldSelectionPolicy.TrySelect(
            systemTextHost: false,
            hasUiField: true,
            ui,
            hasNativeField: true,
            native,
            out var selected));
        Assert.Equal(native.Caret, selected.Caret);
    }

    [Fact]
    public void Trusted_uia_caret_beats_stale_native_on_another_dialog()
    {
        // 日志 16:21:40：UIA 已是新对话框 (1016,931)，原生还停在上一轮 (986,106)。
        var ui = new InputField(
            new IntPtr(10),
            Rect(1016, 931, 1018, 957),
            default,
            CaretIsTrusted: true);
        var native = new InputField(
            new IntPtr(10),
            Rect(986, 106, 988, 132),
            default);

        Assert.True(InputFieldSelectionPolicy.TrySelect(
            systemTextHost: false,
            hasUiField: true,
            ui,
            hasNativeField: true,
            native,
            out var selected));
        Assert.Equal(ui.Caret, selected.Caret);
    }

    [Fact]
    public void Taskbar_search_rejects_start_menu_caret_on_shared_searchhost()
    {
        var host = Rect(0, 0, 800, 1080);
        var startMenu = Rect(168, 87, 170, 111);
        var taskbar = Rect(118, 1037, 120, 1060);

        Assert.False(SearchCaretPolicy.Matches(
            PointerInvocationOrigin.TaskbarSearch, startMenu, host));
        Assert.True(SearchCaretPolicy.Matches(
            PointerInvocationOrigin.TaskbarSearch, taskbar, host));
        Assert.True(SearchCaretPolicy.Matches(
            PointerInvocationOrigin.StartMenuSurface, startMenu, host));
        Assert.False(SearchCaretPolicy.Matches(
            PointerInvocationOrigin.StartMenuSurface, taskbar, host));
    }

    [Fact]
    public void Word_uses_native_tsf_caret_without_a_uia_edit_control()
    {
        var native = new InputField(new IntPtr(10), Rect(520, 640, 522, 664), default);

        Assert.True(InputFieldSelectionPolicy.TrySelect(
            systemTextHost: false,
            hasUiField: false,
            default,
            hasNativeField: true,
            native,
            out var selected));
        Assert.Equal(native, selected);
    }

    [Fact]
    public void Desktop_native_and_uia_fallback_share_stable_foreground_surface()
    {
        var native = new InputField(
            new IntPtr(10),
            Rect(520, 640, 522, 664),
            default,
            Context: new InputContextKey(10, 4, 200, 0));
        var fallback = new InputField(
            new IntPtr(20),
            Rect(520, 640, 522, 664),
            default,
            Context: InputContextKey.ForFocus(new IntPtr(20), 7));

        var normalizedNative = InputFieldSelectionPolicy.NormalizeDesktopSurface(
            systemTextHost: false,
            native,
            foregroundRoot: new IntPtr(20),
            focusGeneration: 7);
        var normalizedFallback = InputFieldSelectionPolicy.NormalizeDesktopSurface(
            systemTextHost: false,
            fallback,
            foregroundRoot: new IntPtr(20),
            focusGeneration: 7);

        Assert.Equal(native.Caret, normalizedNative.Caret);
        Assert.Equal(new IntPtr(20), normalizedNative.TopLevel);
        Assert.Equal(normalizedFallback.Context, normalizedNative.Context);
        Assert.False(KeyboardPositionSession.ShouldRestart(
            visible: true,
            currentHost: normalizedNative.TopLevel,
            nextHost: normalizedFallback.TopLevel,
            currentContext: normalizedNative.Context,
            nextContext: normalizedFallback.Context));
    }

    [Fact]
    public void Same_line_holds_position_but_new_line_repositions_immediately()
    {
        Assert.True(KeyboardPositionSession.ShouldHoldForSameLine(
            visible: true,
            sameHost: true,
            sameContext: true,
            hasPosition: true,
            previousAnchor: Rect(200, 400, 820, 760),
            nextAnchor: Rect(440, 402, 1060, 762)));
        Assert.False(KeyboardPositionSession.ShouldHoldForSameLine(
            visible: true,
            sameHost: true,
            sameContext: true,
            hasPosition: true,
            previousAnchor: Rect(200, 400, 820, 760),
            nextAnchor: Rect(200, 426, 820, 786)));
        Assert.False(KeyboardPositionSession.ShouldFollowTypingLine(
            Rect(200, 400, 202, 422),
            Rect(240, 402, 242, 424)));
        Assert.True(KeyboardPositionSession.ShouldFollowTypingLine(
            Rect(200, 400, 202, 422),
            Rect(200, 426, 202, 448)));
        var held = KeyboardPositionSession.PinHorizontal(
            Rect(200, 400, 820, 760),
            Rect(440, 448, 1060, 808));
        Assert.Equal(200, held.Left);
        Assert.Equal(448, held.Top);
        Assert.Equal(620, held.Width);
        Assert.Equal(820, held.Right);
        Assert.False(KeyboardPositionSession.ShouldMoveVisibleWindow(
            sameRect: true,
            hostModeChanged: false));
        Assert.True(KeyboardPositionSession.ShouldMoveVisibleWindow(
            sameRect: false,
            hostModeChanged: false));
        Assert.True(KeyboardPositionSession.ShouldMoveVisibleWindow(
            sameRect: true,
            hostModeChanged: true));
        var box = new NativeRect { Left = 400, Top = 1060, Right = 900, Bottom = 1120 };
        var caret = new NativeRect { Left = 748, Top = 1080, Right = 750, Bottom = 1110 };
        var other = new NativeRect { Left = 419, Top = 959, Right = 421, Bottom = 997 };
        Assert.True(KeyboardPositionSession.CaretBelongsToAuthorizedField(
            box, caret, caret, box));
        Assert.False(KeyboardPositionSession.CaretBelongsToAuthorizedField(
            box, caret, other, default));
        Assert.False(KeyboardPositionSession.ShouldReplaceAuthorizedField(
            box,
            caret,
            other,
            default,
            incomingFromClicked: false,
            nativeAndUiAgree: false,
            nativeOnly: false));
        Assert.True(KeyboardPositionSession.ShouldReplaceAuthorizedField(
            box,
            caret,
            other,
            default,
            incomingFromClicked: false,
            nativeAndUiAgree: true,
            nativeOnly: false));
        Assert.True(KeyboardPositionSession.ShouldReplaceAuthorizedField(
            box,
            caret,
            other,
            default,
            incomingFromClicked: false,
            nativeAndUiAgree: false,
            nativeOnly: false,
            focusEntered: true,
            incomingCaretTrusted: true));
        Assert.True(KeyboardPositionSession.ShouldReplaceAuthorizedField(
            box,
            caret,
            other,
            default,
            incomingFromClicked: false,
            nativeAndUiAgree: false,
            nativeOnly: false,
            authorizedFieldId: "search",
            incomingFieldId: "address"));
        Assert.True(KeyboardPositionSession.ShouldReplaceAuthorizedField(
            box,
            caret,
            other,
            default,
            incomingFromClicked: false,
            nativeAndUiAgree: false,
            nativeOnly: false,
            manualTap: true));
        Assert.False(KeyboardPositionSession.ShouldFollowTypingLine(
            caret,
            new NativeRect { Left = 760, Top = 1080, Right = 762, Bottom = 1110 }));
        Assert.True(KeyboardPositionSession.ShouldFollowCaretTap(
            caret,
            new NativeRect { Left = 760, Top = 1080, Right = 762, Bottom = 1110 }));
        var sameLineTap = new NativeRect { Left = 760, Top = 1080, Right = 762, Bottom = 1110 };
        var otherFieldCaret = new NativeRect { Left = 485, Top = 1142, Right = 487, Bottom = 1172 };
        Assert.False(KeyboardPositionSession.LooksLikeAnotherField(
            box, caret, sameLineTap, box));
        Assert.True(KeyboardPositionSession.LooksLikeAnotherField(
            box, caret, otherFieldCaret, default));
        Assert.False(KeyboardPositionSession.ShouldHideWhenTapLeavesAuthorizedField(
            alreadyVisible: true,
            hasExternalGesture: true,
            caretBelongs: false,
            anotherField: false));
        Assert.True(KeyboardPositionSession.ShouldHideWhenTapLeavesAuthorizedField(
            alreadyVisible: true,
            hasExternalGesture: true,
            caretBelongs: false,
            anotherField: true));
        Assert.False(KeyboardPositionSession.ShouldHideWhenTapLeavesAuthorizedField(
            alreadyVisible: true,
            hasExternalGesture: true,
            caretBelongs: true,
            anotherField: true));
        Assert.False(KeyboardPositionSession.ShouldHideWhenTapLeavesAuthorizedField(
            alreadyVisible: true,
            hasExternalGesture: true,
            caretBelongs: false,
            anotherField: true,
            surfaceChanged: true));
        Assert.False(KeyboardPositionSession.ShouldHideWhenTapLeavesAuthorizedField(
            alreadyVisible: true,
            hasExternalGesture: true,
            caretBelongs: false,
            anotherField: true,
            searchSession: true));
        Assert.False(KeyboardPositionSession.ShouldFollowTypingLine(
            Rect(200, 400, 202, 422),
            Rect(200, 526, 480, 548),
            previousIsInsertion: true,
            nextIsInsertion: false));
    }

    [Fact]
    public void Layout_follows_the_caret_only_while_inputting()
    {
        Assert.True(KeyboardAnchorPolicy.ShouldFollowInput(
            pinned: false,
            hasInputCaret: true));
        Assert.False(KeyboardAnchorPolicy.ShouldFollowInput(
            pinned: false,
            hasInputCaret: false));
        Assert.False(KeyboardAnchorPolicy.ShouldFollowInput(
            pinned: true,
            hasInputCaret: true));
        Assert.True(KeyboardAnchorPolicy.ShouldClearDragAnchorOnUnlock(
            wasPinned: true,
            nowPinned: false));
        Assert.False(KeyboardAnchorPolicy.ShouldClearDragAnchorOnUnlock(
            wasPinned: false,
            nowPinned: false));
        Assert.True(KeyboardAnchorPolicy.ShouldRelayoutOnUnlock(
            nowPinned: false,
            hasInputCaret: true));
        Assert.False(KeyboardAnchorPolicy.ShouldRelayoutOnUnlock(
            nowPinned: false,
            hasInputCaret: false));
        Assert.True(KeyboardPinPolicy.ShouldHideOnUnlock(
            nowPinned: false,
            stillAuthorized: false));
        Assert.False(KeyboardPinPolicy.ShouldHideOnUnlock(
            nowPinned: false,
            stillAuthorized: true));
    }

    [Fact]
    public void Pinned_keyboard_ignores_auto_hide_and_auto_move()
    {
        Assert.True(KeyboardPinPolicy.ShouldAutoHide(false));
        Assert.False(KeyboardPinPolicy.ShouldAutoHide(true));
        Assert.True(KeyboardPinPolicy.ShouldKeepSessionPosition(
            pinned: true,
            hasPosition: true,
            repositionRequested: true));
        Assert.False(KeyboardPinPolicy.ShouldKeepSessionPosition(
            pinned: false,
            hasPosition: true,
            repositionRequested: true));
        Assert.False(KeyboardPinPolicy.ShouldRestart(pinned: true, wouldRestart: true));
        Assert.True(KeyboardPinPolicy.ShouldRestart(pinned: false, wouldRestart: true));
        Assert.False(KeyboardPinPolicy.ShouldHideForEmptyRect(pinned: true, rectEmpty: true));
        Assert.True(KeyboardPinPolicy.ShouldHideForEmptyRect(pinned: false, rectEmpty: true));
    }

    [Fact]
    public void Input_session_moves_only_after_explicit_reposition_request()
    {
        Assert.True(KeyboardPositionSession.ShouldKeepSessionPosition(
            hasPosition: true,
            repositionRequested: false));
        Assert.False(KeyboardPositionSession.ShouldKeepSessionPosition(
            hasPosition: true,
            repositionRequested: true));
        Assert.False(KeyboardPositionSession.ShouldKeepSessionPosition(
            hasPosition: false,
            repositionRequested: false));
    }

    [Fact]
    public void New_context_restarts_position_even_when_host_is_unchanged()
    {
        var oldContext = new InputContextKey(10, 3, 100, 0);
        var newContext = new InputContextKey(10, 4, 200, 0);

        Assert.True(KeyboardPositionSession.ShouldRestart(
            visible: true,
            currentHost: new IntPtr(50),
            nextHost: new IntPtr(50),
            currentContext: oldContext,
            nextContext: newContext));
        Assert.False(KeyboardPositionSession.ShouldHoldForSameLine(
            visible: true,
            sameHost: true,
            sameContext: false,
            hasPosition: true,
            previousAnchor: Rect(200, 400, 820, 760),
            nextAnchor: Rect(200, 401, 820, 761)));
    }

    [Fact]
    public void New_epoch_on_same_native_view_does_not_flash_visible_keyboard()
    {
        var oldContext = new InputContextKey(10, 3, 100, 0);
        var newContext = new InputContextKey(10, 4, 100, 0);

        Assert.True(KeyboardPositionSession.IsSameSurfaceContext(oldContext, newContext));
        Assert.False(KeyboardPositionSession.ShouldRestart(
            visible: true,
            currentHost: new IntPtr(50),
            nextHost: new IntPtr(50),
            currentContext: oldContext,
            nextContext: newContext));
    }

    [Fact]
    public void Manual_refocus_generation_still_restarts_same_uia_surface()
    {
        var oldContext = InputContextKey.ForFocus(new IntPtr(50), 3);
        var newContext = InputContextKey.ForFocus(new IntPtr(50), 4);

        Assert.False(KeyboardPositionSession.IsSameSurfaceContext(oldContext, newContext));
        Assert.True(KeyboardPositionSession.ShouldRestart(
            visible: true,
            currentHost: new IntPtr(50),
            nextHost: new IntPtr(50),
            currentContext: oldContext,
            nextContext: newContext));
    }

    private static NativeRect Rect(int l, int t, int r, int b) => new()
    {
        Left = l,
        Top = t,
        Right = r,
        Bottom = b
    };
}
