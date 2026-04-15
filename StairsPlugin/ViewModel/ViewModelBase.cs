using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace StairsPlugin.ViewModel
{
    public class ViewModelBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public void OnPropertyChanged([CallerMemberName] string name = null)=>
            PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name));

        public bool SetField<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field,value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }
}
