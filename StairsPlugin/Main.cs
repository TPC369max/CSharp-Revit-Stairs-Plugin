using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StairsPlugin
{
    [Transaction(TransactionMode.Manual)]
    public class CommandStairGenerator : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
                              ref string message, ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            var vm = new StairsPlugin.ViewModel.ViewModel();
            // 把 Revit 的标高列表注入 ViewModel
            foreach (var lv in RevitLevelTools.GetLevels(uiDoc.Document))
                vm.Levels.Add(lv);
            if (vm.Levels.Count > 0)
                vm.BaseLevel = vm.Levels[0];
            if (vm.Levels.Count > 1)
                vm.TopLevel = vm.Levels[1];

            var win = new StairGeneratorWindow(uiDoc, vm);
            if (win.ShowDialog() != true)
                return Result.Cancelled;

            // ---- 从 ViewModel 读参数，调用生成逻辑 ----
            using var scope = new StairsEditScope(
                uiDoc.Document, "自动生成双跑楼梯");
            // ... 后续楼梯生成代码（使用 vm 中的所有属性）


            return Result.Succeeded;
        }
    }
}
