using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace StairsPlugin
{
    [Transaction(TransactionMode.Manual)]
    internal class CommandStairManager : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc=uiDoc.Document;

            try
            {
                List<StairsType> stairsTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(StairsType))
                    .Cast<StairsType>()
                    .ToList();

                StringBuilder sb = new StringBuilder();
                sb.AppendLine($"当前项目中共有 {stairsTypes.Count} 种楼梯类型：");
                foreach (var st in stairsTypes)
                {
                    sb.AppendLine($"- {st.Name} (ID: {st.Id})");
                }

                string newTypeName = "自定义楼梯333";
                StairsType newType = null;

                
                using (Transaction tr = new Transaction(doc, "创建楼梯类型"))
                {
                    tr.Start();

                    bool exists = stairsTypes.Any(x => x.Name == newTypeName);
                    if (!exists && stairsTypes.Count > 0)
                    {
                        StairsType templateType = stairsTypes.First();
                        newType = templateType.Duplicate(newTypeName) as StairsType;

                        foreach (Parameter para in newType.Parameters)
                        {
                            // 打印参数的界面名称和对应的枚举 ID
                            string name = para.Definition.Name;
                            InternalDefinition def = para.Definition as InternalDefinition;
                            BuiltInParameter bip = def.BuiltInParameter;
                            sb.AppendLine($"名称: {name}, 枚举: {bip}");
                        }

                        Parameter p = newType.get_Parameter(BuiltInParameter.STAIRS_ATTR_MAX_RISER_HEIGHT);
                        if (p != null && !p.IsReadOnly)
                        {
                            p.Set(180 / 304.8); // 将 180mm 转换为英尺
                        }
                        sb.AppendLine($"\n成功新建类型: {newTypeName}");
                    }
                    else if (exists)
                    {
                        sb.AppendLine($"\n类型 '{newTypeName}' 已存在，跳过创建。");
                    }
                    tr.Commit();
                }
                TaskDialog.Show("楼梯类型管理", sb.ToString());
                
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
