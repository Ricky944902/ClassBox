// MVVM 基础设施已迁移至 ClassBox.Abstractions.Mvvm（主程序与插件共用同一套基类）。
// 使用处请添加：using ClassBox.Abstractions.Mvvm;
using ClassBox.Abstractions.Mvvm;
using ClassBox.ViewModels;

namespace ClassBox.ViewModels;

internal static class MvvmTypes
{
    internal static Type[] All => new[] { typeof(ObservableObject), typeof(RelayCommand), typeof(AsyncRelayCommand) };
}
