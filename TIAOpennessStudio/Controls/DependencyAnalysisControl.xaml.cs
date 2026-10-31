using System.Windows.Controls;
using Prism.Ioc;
using TIAOpennessStudio.Services.Abstractions;
using TIAOpennessStudio.ViewModels;

namespace TIAOpennessStudio.Controls
{
    public partial class DependencyAnalysisControl : UserControl
    {
        public DependencyAnalysisControl()
        {
            InitializeComponent();

            try
            {
                var workerHost = App.Services?.Resolve<IProjectKnowledgeWorkerHostService>();
                if (workerHost != null)
                    DataContext = new DependencyAnalysisViewModel(workerHost);
            }
            catch
            {
                // Silently degrade during designer preview or before the service is initialized
            }
        }
    }
}
