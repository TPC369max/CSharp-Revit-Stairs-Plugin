using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace StairsPlugin.ViewModel
{
    public class RelayCommand : ICommand
    {
        Action _execute;
        Func<bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute=null)
        {
            _execute= execute;
            _canExecute= canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add {CommandManager.RequerySuggested += value;}
            remove
            {
                CommandManager.RequerySuggested -= value;
            }
        }

        public bool CanExecute(object parameter)=>_canExecute?.Invoke()??true;

        public void Execute(object parameter) => _execute();

        public void RaiseCanExecuteChanged() =>CommandManager.InvalidateRequerySuggested();

    }
}
