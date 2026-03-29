using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AIDeskAssistant.Models;
using AIDeskAssistant.Services;

namespace AIDeskAssistant.Platform.MacOS;

[SupportedOSPlatform("macos")]
internal sealed class MacOSMouseService : IMouseService
{
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern int CGEventPost(CGEventTapLocation tap, IntPtr evt);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern IntPtr CGEventCreateMouseEvent(IntPtr source, CGEventType mouseType, CGPoint mouseCursorPosition, CGMouseButton mouseButton);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern IntPtr CGEventCreateScrollWheelEvent(IntPtr source, CGScrollEventUnit units, uint wheelCount, int wheel1);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern void CGDisplayMoveCursorToPoint(IntPtr display, CGPoint point);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern IntPtr CGMainDisplayID();

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern CGPoint CGEventGetLocation(IntPtr evt);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern IntPtr CGEventCreate(IntPtr source);

    private enum CGEventTapLocation { HID = 0 }
    private enum CGScrollEventUnit { Pixel = 0, Line = 1 }
    private enum CGMouseButton { Left = 0, Right = 1, Center = 2 }

    private enum CGEventType
    {
        MouseMoved    = 5,
        LeftMouseDown = 1, LeftMouseUp  = 2,
        RightMouseDown = 3, RightMouseUp = 4,
        OtherMouseDown = 25, OtherMouseUp = 26,
        ScrollWheel   = 22,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint { public double X; public double Y; }

    private const int ClickDelayMs       = 30;
    private const int DoubleClickDelayMs = 50;

    public void MoveTo(int x, int y)
    {
        var point = new CGPoint { X = x, Y = y };
        CGDisplayMoveCursorToPoint(CGMainDisplayID(), point);
    }

    public void Click(MouseButton button = MouseButton.Left)
    {
        var pos = GetCurrentCGPoint();
        (CGEventType down, CGEventType up, CGMouseButton btn) = button switch
        {
            MouseButton.Right  => (CGEventType.RightMouseDown,  CGEventType.RightMouseUp,  CGMouseButton.Right),
            MouseButton.Middle => (CGEventType.OtherMouseDown,  CGEventType.OtherMouseUp,  CGMouseButton.Center),
            _                  => (CGEventType.LeftMouseDown,   CGEventType.LeftMouseUp,   CGMouseButton.Left),
        };
        PostMouseEvent(down, pos, btn);
        PostMouseEvent(up,   pos, btn);
    }

    public void ClickAt(int x, int y, MouseButton button = MouseButton.Left)
    {
        MoveTo(x, y);
        Thread.Sleep(ClickDelayMs);
        Click(button);
    }

    public void DoubleClick(int x, int y)
    {
        ClickAt(x, y);
        Thread.Sleep(DoubleClickDelayMs);
        ClickAt(x, y);
    }

    public void Scroll(int delta)
    {
        IntPtr evt = CGEventCreateScrollWheelEvent(IntPtr.Zero, CGScrollEventUnit.Line, 1, delta);
        try { CGEventPost(CGEventTapLocation.HID, evt); }
        finally { CFRelease(evt); }
    }

    public (int X, int Y) GetPosition()
    {
        IntPtr evt = CGEventCreate(IntPtr.Zero);
        try
        {
            CGPoint pos = CGEventGetLocation(evt);
            return ((int)pos.X, (int)pos.Y);
        }
        finally { CFRelease(evt); }
    }

    private CGPoint GetCurrentCGPoint()
    {
        var (x, y) = GetPosition();
        return new CGPoint { X = x, Y = y };
    }

    private static void PostMouseEvent(CGEventType type, CGPoint pos, CGMouseButton btn)
    {
        IntPtr evt = CGEventCreateMouseEvent(IntPtr.Zero, type, pos, btn);
        try { CGEventPost(CGEventTapLocation.HID, evt); }
        finally { CFRelease(evt); }
    }
}
