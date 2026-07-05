using System;
using System.Linq;
using System.Reflection;

namespace OutlandersMultiplayer.Mod.Overlay;

public sealed class ReflectionGui
{
    private readonly Type? _rectType;
    private readonly Type? _guiType;
    private readonly Type? _colorType;
    private readonly MethodInfo? _guiButton;
    private readonly MethodInfo? _guiBox;
    private readonly MethodInfo? _guiLabel;
    private readonly MethodInfo? _guiTextField;
    private readonly PropertyInfo? _guiColor;
    private readonly PropertyInfo? _guiBackgroundColor;
    private readonly PropertyInfo? _guiContentColor;

    public ReflectionGui()
    {
        _rectType = ResolveType("UnityEngine.Rect");
        _colorType = ResolveType("UnityEngine.Color");
        _guiType = ResolveType("UnityEngine.GUI");
        _guiButton = FindRectString(_guiType, "Button");
        _guiBox = FindRectString(_guiType, "Box");
        _guiLabel = FindRectString(_guiType, "Label");
        _guiTextField = FindTextField(_guiType);
        _guiColor = _guiType?.GetProperty("color", BindingFlags.Public | BindingFlags.Static);
        _guiBackgroundColor = _guiType?.GetProperty("backgroundColor", BindingFlags.Public | BindingFlags.Static);
        _guiContentColor = _guiType?.GetProperty("contentColor", BindingFlags.Public | BindingFlags.Static);
    }

    public bool Button(float x, float y, float width, float height, string text)
    {
        if (_guiButton == null || _rectType == null)
        {
            return false;
        }

        var rect = Activator.CreateInstance(_rectType, x, y, width, height);
        return (bool)(_guiButton.Invoke(null, new[] { rect, text }) ?? false);
    }

    public void Box(float x, float y, float width, float height, string text)
    {
        if (_guiBox == null || _rectType == null) return;
        var rect = Activator.CreateInstance(_rectType, x, y, width, height);
        _guiBox.Invoke(null, new[] { rect, text });
    }

    public void Label(float x, float y, float width, float height, string text)
    {
        if (_guiLabel == null || _rectType == null) return;
        var rect = Activator.CreateInstance(_rectType, x, y, width, height);
        _guiLabel.Invoke(null, new[] { rect, text });
    }

    public string TextField(float x, float y, float width, float height, string text, int maxLength)
    {
        if (_guiTextField == null || _rectType == null) return text;
        var rect = Activator.CreateInstance(_rectType, x, y, width, height);
        return (string)(_guiTextField.Invoke(null, new object?[] { rect, text, maxLength }) ?? text);
    }

    public void SetColor(float r, float g, float b, float a = 1f)
    {
        SetUnityColor(_guiColor, r, g, b, a);
    }

    public void SetBackgroundColor(float r, float g, float b, float a = 1f)
    {
        SetUnityColor(_guiBackgroundColor, r, g, b, a);
    }

    public void SetContentColor(float r, float g, float b, float a = 1f)
    {
        SetUnityColor(_guiContentColor, r, g, b, a);
    }

    public void ResetColors()
    {
        SetColor(1f, 1f, 1f, 1f);
        SetBackgroundColor(1f, 1f, 1f, 1f);
        SetContentColor(1f, 1f, 1f, 1f);
    }

    private void SetUnityColor(PropertyInfo? property, float r, float g, float b, float a)
    {
        if (property == null || _colorType == null)
        {
            return;
        }

        var color = Activator.CreateInstance(_colorType, r, g, b, a);
        property.SetValue(null, color);
    }

    private static Type? ResolveType(string name)
    {
        return Type.GetType($"{name}, UnityEngine.CoreModule")
            ?? Type.GetType($"{name}, UnityEngine.IMGUIModule")
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(name, throwOnError: false))
                .FirstOrDefault(type => type != null);
    }

    private static MethodInfo? Find(Type? type, string name, int parameterCount)
    {
        return type?.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method => method.Name == name && method.GetParameters().Length == parameterCount);
    }

    private static MethodInfo? FindRectString(Type? type, string name)
    {
        return type?.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method =>
            {
                if (method.Name != name) return false;
                var parameters = method.GetParameters();
                return parameters.Length == 2 && parameters[1].ParameterType == typeof(string);
            });
    }

    private static MethodInfo? FindTextField(Type? type)
    {
        return type?.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method =>
            {
                if (method.Name != "TextField") return false;
                var parameters = method.GetParameters();
                return parameters.Length == 3
                    && parameters[1].ParameterType == typeof(string)
                    && parameters[2].ParameterType == typeof(int);
            });
    }
}
