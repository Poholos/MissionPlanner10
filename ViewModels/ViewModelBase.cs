using CommunityToolkit.Mvvm.ComponentModel;

namespace MissionPlanner.ViewModels;

public abstract class ViewModelBase : ObservableObject { }

public interface IActivationAware {
  void Activate();
}

public interface IDeactivationAware {
  void Deactivate();
}
