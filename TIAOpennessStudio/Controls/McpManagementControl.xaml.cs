using System.Text;
using System.Windows.Controls;
using Prism.Ioc;
using TIAOpennessStudio.Events;
using TIAOpennessStudio.Services;
using TIAOpennessStudio.ViewModels;
using TIAOpennessStudio.Services.Implementations;

namespace TIAOpennessStudio.Controls
{
    public partial class McpManagementControl : UserControl
    {
        public McpManagementControl()
        {
            InitializeComponent();

            // Obtain IMcpAdapter and McpToolRegistrySync via the Prism DI container
            var container = ContainerLocator.Container;
            if (container != null)
            {
                var mcpAdapter = container.Resolve<IMcpAdapter>();
                var mcpToolRegistrySync = container.Resolve<McpToolRegistrySync>();
                DataContext = new McpManagementViewModel(mcpAdapter, mcpToolRegistrySync);
            }
        }

        private void ToolsList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var vm = DataContext as McpManagementViewModel;
            if (vm?.SelectedTool == null) return;

            var tool = vm.SelectedTool;
            var sb = new StringBuilder();
            sb.AppendLine($"// MCP Tool: {tool.Name}");
            sb.AppendLine($"// Description: {tool.Description}");
            sb.AppendLine();
            sb.AppendLine("// Parameters:");
            if (tool.Parameters != null && tool.Parameters.Count > 0)
            {
                foreach (var p in tool.Parameters)
                {
                    var req = p.Required ? " [required]" : "";
                    sb.AppendLine($"//   {p.Name} : {p.Type}{req}");
                    if (!string.IsNullOrWhiteSpace(p.Description))
                        sb.AppendLine($"//       {p.Description}");
                }
            }
            else
            {
                sb.AppendLine("//   (none)");
            }
            sb.AppendLine();
            sb.AppendLine("// Example call:");
            sb.AppendLine("{");
            sb.AppendLine($"  \"name\": \"{tool.Name}\",");
            sb.AppendLine("  \"arguments\": {");
            if (tool.Parameters != null)
            {
                for (int i = 0; i < tool.Parameters.Count; i++)
                {
                    var p = tool.Parameters[i];
                    var comma = i < tool.Parameters.Count - 1 ? "," : "";
                    sb.AppendLine($"    \"{p.Name}\": \"\"{comma}");
                }
            }
            sb.AppendLine("  }");
            sb.AppendLine("}");

            // Open in the middle Code Viewer tab via the Prism EventAggregator
            var container = ContainerLocator.Container;
            if (container != null)
            {
                var eventAggregator = container.Resolve<Prism.Events.IEventAggregator>();
                var codeViewerService = container.Resolve<Services.Abstractions.ICodeViewerService>();
                var editor = codeViewerService.CreateCodeEditor(sb.ToString());
                editor.IsReadOnly = true;
                eventAggregator.GetEvent<ShowCodeViewerEvent>().Publish(
                    new CodeViewerEventArgs($"Tool: {tool.Name}", "MCP", editor));
            }
        }
    }
}
