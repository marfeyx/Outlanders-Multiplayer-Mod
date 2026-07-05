#if !OUTLANDERS_REAL_MELONLOADER
namespace UnityEngine;

public struct Rect
{
    public Rect(float x, float y, float width, float height)
    {
        xMin = x;
        yMin = y;
        this.width = width;
        this.height = height;
    }

    public float xMin;
    public float yMin;
    public float width;
    public float height;
}

public static class GUI
{
    public static bool Button(Rect position, string text)
    {
        return false;
    }

    public static Rect Window(int id, Rect clientRect, WindowFunction func, string text)
    {
        func(id);
        return clientRect;
    }

    public static void DragWindow()
    {
    }
}

public delegate void WindowFunction(int id);

public static class GUILayout
{
    public static void Label(string text)
    {
    }

    public static void Space(float pixels)
    {
    }

    public static string TextField(string text, int maxLength)
    {
        return text;
    }

    public static bool Button(string text)
    {
        return false;
    }

    public static void BeginHorizontal()
    {
    }

    public static void EndHorizontal()
    {
    }
}
#endif
