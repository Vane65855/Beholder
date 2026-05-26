using CommunityToolkit.Mvvm.ComponentModel;

namespace Beholder.Ui.ViewModels;

/// <summary>
/// Observable view-model for the Alerts section of the Settings tab. Three
/// independently-toggleable kill-switches for the alert detector pipeline.
/// All three take effect live (mandatory startup chain verification still
/// runs regardless of <see cref="EnableChainIntegrityMonitor"/> — Phase 13.3
/// hardening). Pure state holder; the toggle commands live on
/// <see cref="SettingsTabViewModel"/> since they need access to
/// <see cref="Services.IDaemonClient"/>.
/// </summary>
internal sealed partial class AlertSettingsRow : ObservableObject {
    [ObservableProperty]
    private bool _enableNewProcessDetection;

    [ObservableProperty]
    private bool _enableHashChangeDetection;

    [ObservableProperty]
    private bool _enableChainIntegrityMonitor;

    /// <summary>Saving flag for the EnableNewProcessDetection pill.</summary>
    [ObservableProperty]
    private bool _isSavingNewProcessDetection;

    /// <summary>Saving flag for the EnableHashChangeDetection pill.</summary>
    [ObservableProperty]
    private bool _isSavingHashChangeDetection;

    /// <summary>Saving flag for the EnableChainIntegrityMonitor pill.</summary>
    [ObservableProperty]
    private bool _isSavingChainIntegrityMonitor;
}
