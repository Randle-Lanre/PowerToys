﻿// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.PowerToys.Settings.UI.Library.Utilities;

// <summary>
//     Application settings.
// </summary>
// <history>
//     2008 created by Truong Do (ductdo).
//     2009-... modified by Truong Do (TruongDo).
//     2023- Included in PowerToys.
// </history>
using Microsoft.Win32;

[module: SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Scope = "member", Target = "MouseWithoutBorders.Properties.Setting.Values.#LoadIntSetting(System.String,System.Int32)", Justification = "Dotnet port with style preservation")]
[module: SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Scope = "member", Target = "MouseWithoutBorders.Properties.Setting.Values.#SaveSetting(System.String,System.Object)", Justification = "Dotnet port with style preservation")]
[module: SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Scope = "member", Target = "MouseWithoutBorders.Properties.Setting.Values.#LoadStringSetting(System.String,System.String)", Justification = "Dotnet port with style preservation")]
[module: SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Scope = "member", Target = "MouseWithoutBorders.Properties.Setting.Values.#SaveSettingQWord(System.String,System.Int64)", Justification = "Dotnet port with style preservation")]

namespace MouseWithoutBorders.Class
{
    internal class Settings
    {
        internal bool Changed;

        private readonly ISettingsUtils _settingsUtils;
        private readonly object _loadingSettingsLock = new object();
        private readonly IFileSystemWatcher _watcher;

        private MouseWithoutBordersProperties _properties;
        private MouseWithoutBordersSettings _settings;

        // Avoid instantly saving every change to the file when updating properties.
        public bool PauseInstantSaving { get; set; }

        private void UpdateSettingsFromJson()
        {
            try
            {
                if (!_settingsUtils.SettingsExists("MouseWithoutBorders"))
                {
                    var defaultSettings = new MouseWithoutBordersSettings();
                    if (!Common.RunOnLogonDesktop)
                    {
                        defaultSettings.Save(_settingsUtils);
                    }
                }

                var settings = _settingsUtils.GetSettingsOrDefault<MouseWithoutBordersSettings>("MouseWithoutBorders");
                if (settings != null)
                {
                    PauseInstantSaving = true;

                    var last_properties = _properties;

                    _settings = settings;

                    _properties = settings.Properties;

                    // Keep track of the need to resend the machine matrix.
                    bool shouldSendMachineMatrix = false;

                    // Keep track of the need to save into the settings file.
                    bool shouldSaveNewSettingsValues = false;

                    if (last_properties != null)
                    {
                        // Same as in CheckBoxCircle_CheckedChanged
                        if (last_properties.WrapMouse != _settings.Properties.WrapMouse)
                        {
                            shouldSendMachineMatrix = true;
                        }

                        // Same as CheckBoxDrawMouse_CheckedChanged
                        if (last_properties.DrawMouseCursor != _settings.Properties.DrawMouseCursor && !_settings.Properties.DrawMouseCursor)
                        {
                            CustomCursor.ShowFakeMouseCursor(int.MinValue, int.MinValue);
                        }

                        if (!Enumerable.SequenceEqual(last_properties.MachineMatrixString, _settings.Properties.MachineMatrixString))
                        {
                            _properties.MachineMatrixString = _settings.Properties.MachineMatrixString;
                            Common.MachineMatrix = null; // Forces read next time it's needed.
                            shouldSendMachineMatrix = true;
                        }

                        var shouldReopenSockets = false;

                        if (Common.MyKey != _properties.SecurityKey.Value)
                        {
                            Common.MyKey = _properties.SecurityKey.Value;
                            shouldReopenSockets = true;
                        }

                        if (shouldReopenSockets)
                        {
                            SocketStuff.InvalidKeyFound = false;
                            Common.ReopenSocketDueToReadError = true;
                            Common.ReopenSockets(true);
                        }

                        if (shouldSendMachineMatrix)
                        {
                            Common.SendMachineMatrix();
                            shouldSaveNewSettingsValues = true;
                        }

                        if (shouldSaveNewSettingsValues)
                        {
                            SaveSettings();
                        }
                    }
                }
            }
            catch (IOException ex)
            {
                Logger.LogEvent($"Failed to read settings: {ex.Message}", System.Diagnostics.EventLogEntryType.Error);
            }

            PauseInstantSaving = false;
        }

        public void SaveSettings()
        {
            if (!Common.RunOnLogonDesktop)
            {
                SaveSettingsToJson((MouseWithoutBordersProperties)_properties.Clone());
            }
        }

        private void SaveSettingsToJson(MouseWithoutBordersProperties properties_to_save)
        {
            _settings.Properties = properties_to_save;
            _ = Task.Factory.StartNew(
                () =>
            {
                bool saved = false;

                for (int i = 0; i < 5; ++i)
                {
                    try
                    {
                        lock (_loadingSettingsLock)
                        {
                            _settings.Save(_settingsUtils);
                        }

                        saved = true;
                    }
                    catch (IOException ex)
                    {
                        Logger.LogEvent($"Failed to write settings: {ex.Message}", System.Diagnostics.EventLogEntryType.Error);
                    }

                    if (saved)
                    {
                        break;
                    }
                    else
                    {
                        Thread.Sleep(500);
                    }
                }
            },
                System.Threading.CancellationToken.None,
                TaskCreationOptions.None,
                TaskScheduler.Default);
        }

        internal Settings()
        {
            _settingsUtils = new SettingsUtils();

            _watcher = Helper.GetFileWatcher("MouseWithoutBorders", "settings.json", () =>
            {
                try
                {
                    UpdateSettingsFromJson();
                }
                catch (Exception ex)
                {
                    Logger.LogEvent($"Failed to update settings: {ex.Message}", System.Diagnostics.EventLogEntryType.Error);
                }
            });

            UpdateSettingsFromJson();
        }

        internal string Username { get; set; }

        internal bool IsMyKeyRandom { get; set; }

        internal string MachineMatrixString
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return string.Join(",", _properties.MachineMatrixString);
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.MachineMatrixString = new List<string>(value.Split(","));
                    if (!PauseInstantSaving)
                    {
                        SaveSettings();
                    }
                }
            }
        }

        internal string MachinePoolString
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.MachinePool.Value;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    if (!value.Equals(_properties.MachinePool.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        _properties.MachinePool.Value = value;
                    }
                }
            }
        }

        internal string MyID => Application.ProductName + " Application";

        internal string MyIdEx => Application.ProductName + " Application-Ex";

        internal bool ShareClipboard
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.ShareClipboard;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.ShareClipboard = value;
                }
            }
        }

        internal bool TransferFile
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.TransferFile;
                }
            }

            set
            {
                _properties.TransferFile = value;
            }
        }

        internal bool MatrixOneRow
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.MatrixOneRow;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.MatrixOneRow = true;
                    if (!PauseInstantSaving)
                    {
                        SaveSettings();
                    }
                }
            }
        }

        internal bool MatrixCircle
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.WrapMouse;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.WrapMouse = value;
                    if (!PauseInstantSaving)
                    {
                        SaveSettings();
                    }
                }
            }
        }

        internal int EasyMouse
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.EasyMouse.Value;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.EasyMouse.Value = value;
                    if (!PauseInstantSaving)
                    {
                        // Easy Mouse can be enabled or disabled through a shortcut, so a save is required.
                        SaveSettings();
                    }
                }
            }
        }

        internal bool BlockMouseAtCorners
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.BlockMouseAtScreenCorners;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.BlockMouseAtScreenCorners = value;
                }
            }
        }

        internal string Enc(string st, bool dec, DataProtectionScope protectionScope)
        {
            if (st == null || st.Length < 1)
            {
                return string.Empty;
            }

            byte[] ep = Common.GetBytesU(st);
            byte[] rv, st2;

            if (dec)
            {
                st2 = Convert.FromBase64String(st);
                rv = ProtectedData.Unprotect(st2, ep, protectionScope);
                return Common.GetStringU(rv);
            }
            else
            {
                st2 = Common.GetBytesU(st);
                rv = ProtectedData.Protect(st2, ep, protectionScope);
                return Convert.ToBase64String(rv);
            }
        }

        internal string MyKey
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    if (_properties.SecurityKey.Value.Length != 0)
                    {
                        Common.LogDebug("GETSECKEY: Key was already loaded/set: " + _properties.SecurityKey.Value);
                        return _properties.SecurityKey.Value;
                    }
                    else
                    {
                        string randomKey = Common.CreateDefaultKey();
                        _properties.SecurityKey.Value = randomKey;

                        return randomKey;
                    }
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.SecurityKey.Value = value;
                    if (!PauseInstantSaving)
                    {
                        SaveSettings();
                    }
                }
            }
        }

        internal int MyKeyDaysToExpire
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return int.MaxValue; // TODO(@yuyoyuppe): do we still need expiration mechanics now?
                }
            }
        }

        internal bool DisableCAD
        {
            get
            {
                return false;
            }
        }

        internal bool HideLogonLogo
        {
            get
            {
                return false;
            }
        }

        internal bool HideMouse
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.HideMouseAtScreenEdge;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.HideMouseAtScreenEdge = value;
                }
            }
        }

        internal bool BlockScreenSaver
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.BlockScreenSaverOnOtherMachines;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.BlockScreenSaverOnOtherMachines = value;
                }
            }
        }

        internal bool BlockScreenSaverEx
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.BlockScreenSaverOnOtherMachines;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.BlockScreenSaverOnOtherMachines = value;
                }
            }
        }

        internal bool MoveMouseRelatively
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.MoveMouseRelatively;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.MoveMouseRelatively = value;
                }
            }
        }

        internal string LastPersonalizeLogonScr
        {
            get
            {
                return string.Empty;
            }
        }

        internal uint DesMachineID
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return (uint)_properties.MachineID.Value;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.MachineID.Value = (int)value;
                }
            }
        }

        internal int LastX
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.LastX.Value;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    Common.LastX = value;
                    _properties.LastX.Value = value;
                }
            }
        }

        internal int LastY
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.LastY.Value;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    Common.LastY = value;
                    _properties.LastY.Value = value;
                }
            }
        }

        internal int PackageID
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.PackageID.Value;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.PackageID.Value = value;
                }
            }
        }

        internal bool FirstRun
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.FirstRun;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.FirstRun = value;
                    if (!PauseInstantSaving)
                    {
                        SaveSettings();
                    }
                }
            }
        }

        internal int HotKeySwitchMachine
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.HotKeySwitchMachine.Value;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.HotKeySwitchMachine.Value = value;
                }
            }
        }

        internal int HotKeyToggleEasyMouse
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.HotKeyToggleEasyMouse.Value;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.HotKeyToggleEasyMouse.Value = value;
                }
            }
        }

        internal int HotKeyLockMachine
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.HotKeyLockMachine.Value;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.HotKeyLockMachine.Value = value;
                }
            }
        }

        internal int HotKeyReconnect
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.HotKeyReconnect.Value;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.HotKeyReconnect.Value = value;
                }
            }
        }

        internal int HotKeyCaptureScreen
        {
            get
            {
                return 0;
            }
        }

        internal int HotKeyExitMM
        {
            get
            {
                return 0;
            }
        }

        internal int HotKeySwitch2AllPC
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.HotKeySwitch2AllPC.Value;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.HotKeySwitch2AllPC.Value = value;
                }
            }
        }

        private int switchCount;

        internal int SwitchCount
        {
            get
            {
                return switchCount;
            }

            set
            {
                switchCount = value;
                if (!PauseInstantSaving)
                {
                    SaveSettings();
                }
            }
        }

        internal int DumpObjectsLevel => 6;

        internal int TcpPort => _properties.TCPPort.Value;

        internal bool DrawMouse
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.DrawMouseCursor;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.DrawMouseCursor = value;
                }
            }
        }

        internal bool DrawMouseEx
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.DrawMouseEx;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.DrawMouseEx = value;
                }
            }
        }

        internal bool ReverseLookup
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.ValidateRemoteMachineIP;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.ValidateRemoteMachineIP = value;
                }
            }
        }

        internal bool SameSubNetOnly
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.SameSubnetOnly;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.SameSubnetOnly = value;
                }
            }
        }

        internal string Name2IP
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.Name2IP.Value;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.Name2IP.Value = value;
                }
            }
        }

        internal bool UseVKMap
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.UseVKMap;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.UseVKMap = value;
                }
            }
        }

        internal bool FirstCtrlShiftS
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.FirstCtrlShiftS;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.FirstCtrlShiftS = value;
                }
            }
        }

        internal Hashtable VKMap
        {
            get
            {
                return new Hashtable();
            }
        }

        internal bool StealFocusWhenSwitchingMachine => _properties.StealFocusWhenSwitchingMachine;

        private string deviceId;

        internal string DeviceId
        {
            get
            {
                string newGuid = Guid.NewGuid().ToString();

                if (deviceId == null || deviceId.Length != newGuid.Length)
                {
                    string defaultId = newGuid;
                    lock (_loadingSettingsLock)
                    {
                        _properties.DeviceID = defaultId;
                        deviceId = _properties.DeviceID.Value;

                        if (deviceId.Equals(defaultId, StringComparison.OrdinalIgnoreCase))
                        {
                            return _properties.DeviceID.Value;
                        }
                    }
                }

                return deviceId;
            }
        }

        private int? machineId;

        internal int MachineId
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    machineId ??= (machineId = _properties.MachineID.Value).Value;

                    if (machineId == 0)
                    {
                        _properties.MachineID.Value = Common.Ran.Next();
                        machineId = _properties.MachineID.Value;
                    }
                }

                return machineId.Value;
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.MachineID.Value = value;
                }
            }
        }

        internal bool OneWayControlMode => false;

        internal bool OneWayClipboardMode => false;

        internal bool ShowClipNetStatus
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.ShowClipboardAndNetworkStatusMessages;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.ShowClipboardAndNetworkStatusMessages = value;
                }
            }
        }

        internal bool ShowOriginalUI
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.ShowOriginalUI;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.ShowOriginalUI = value;
                }
            }
        }

        internal bool SendErrorLogV2
        {
            get
            {
                return false;
            }
        }
    }

    public static class Setting
    {
        internal static Settings Values = new Settings();
    }
}
