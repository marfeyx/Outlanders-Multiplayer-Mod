#if !OUTLANDERS_REAL_MELONLOADER
using System;

namespace MelonLoader;

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class MelonInfoAttribute : Attribute
{
    public MelonInfoAttribute(Type melonType, string name, string version, string author)
    {
    }
}

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class MelonGameAttribute : Attribute
{
    public MelonGameAttribute(string developer, string game)
    {
    }
}

public abstract class MelonMod
{
    protected MelonMod()
    {
        LoggerInstance = new MelonLogger.Instance();
    }

    public MelonLogger.Instance LoggerInstance { get; }

    public virtual void OnInitializeMelon()
    {
    }

    public virtual void OnUpdate()
    {
    }

    public virtual void OnGUI()
    {
    }

    public virtual void OnApplicationQuit()
    {
    }
}

public static class MelonLogger
{
    public sealed class Instance
    {
        public void Msg(string message)
        {
            Console.WriteLine(message);
        }
    }
}
#endif
