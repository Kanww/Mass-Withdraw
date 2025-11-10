/*
===============================================================================
  MassWithdraw – Configuration.cs
===============================================================================

  Overview
  ---------------------------------------------------------------------------
  Defines the persistent configuration for the MassWithdraw plugin.
  Dalamud automatically saves and loads this file from:
    %AppData%\XIVLauncher\pluginConfigs\MassWithdraw.json
  • Implements Dalamud’s `IPluginConfiguration` interface.
  • Stores user preferences that persist between sessions.
  • Allows the plugin to track configuration version for migration support.
  • Provides a simple `Save()` method to persist runtime changes.

  Usage
  ---------------------------------------------------------------------------
      Plugin.Configuration.AutoOpenOnRetainer = false;
      Plugin.Configuration.Save();

===============================================================================
*/

using Dalamud.Configuration;
using System;

namespace MassWithdraw;

[Serializable]
public class Configuration : IPluginConfiguration
{

    /*
     * ---------------------------------------------------------------------------
     *  Version tracking
     * ---------------------------------------------------------------------------
     *  Used by Dalamud to identify configuration structure versions.
     *  Increment if fields are added or renamed in future releases.
     * ---------------------------------------------------------------------------
    */
    public int Version { get; set; } = 0;

    /*
     * ---------------------------------------------------------------------------
     *  User preferences
     * ---------------------------------------------------------------------------
     *  These fields control persistent user-facing plugin behavior.
     * ---------------------------------------------------------------------------
    */
    public bool IsConfigWindowMovable { get; set; } = true;
    public bool AutoOpenOnRetainer { get; set; } = true;

    /*
     * ---------------------------------------------------------------------------
     *  Persistence helper
     * ---------------------------------------------------------------------------
     *  Call this method to immediately save the configuration state to disk.
     * ---------------------------------------------------------------------------
    */
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
