using Dalamud.Configuration;
using System;

namespace MassWithdraw;

[Serializable]
public class Configuration : IPluginConfiguration
{

    /*
     * Used by Dalamud to identify configuration structure versions
     */
    public int Version { get; set; } = 0;

    /*
     *  Control persistent user-facing plugin behavior.
     */
    public bool IsConfigWindowMovable { get; set; } = true;
    public bool AutoOpenOnRetainer { get; set; } = true;

    public bool AnchorWindow { get; set; } = true;

    /*
     *  Call this method to immediately save the configuration state to disk.
     */
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
