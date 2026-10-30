using CommunityToolkit.Mvvm.ComponentModel;
using Shiny;

namespace MauiSample.Features.Memory;

/// <summary>
/// Everything the assistant has saved about the user, editable and deletable (the AiMemoryView does the work).
/// </summary>
[ShellMap<MemoryPage>("memory")]
public partial class MemoryViewModel : ObservableObject;
