using System.Windows;
using System.Windows.Controls;
using TIAOpennessStudio.Models;

namespace TIAOpennessStudio.Controls
{
    public class MessageBlockTemplateSelector : DataTemplateSelector
    {
        public DataTemplate TextTemplate { get; set; }
        public DataTemplate ThoughtTemplate { get; set; }
        public DataTemplate FileChangeTemplate { get; set; }
        public DataTemplate CommandTemplate { get; set; }
        public DataTemplate ContextRefTemplate { get; set; }
        public DataTemplate StatusTemplate { get; set; }
        public DataTemplate CodeTemplate { get; set; }
        public DataTemplate DeliveryTemplate { get; set; }
        public DataTemplate ProgressTemplate { get; set; }
        public DataTemplate DefaultTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            if (item == null) return DefaultTemplate;

            switch (item)
            {
                case CodeBlock _:
                    return CodeTemplate ?? DefaultTemplate;
                case MessageTextBlock _:
                    return TextTemplate ?? DefaultTemplate;
                case ThoughtBlock _:
                    return ThoughtTemplate ?? DefaultTemplate;
                case FileChangeBlock _:
                    return FileChangeTemplate ?? DefaultTemplate;
                case CommandExecutionBlock _:
                    return CommandTemplate ?? DefaultTemplate;
                case ContextRefBlock _:
                    return ContextRefTemplate ?? DefaultTemplate;
                case AgentStatusBlock _:
                    return StatusTemplate ?? DefaultTemplate;
                case DeliveryBlock _:
                    return DeliveryTemplate ?? DefaultTemplate;
                case PlanProgressBlock _:
                    return ProgressTemplate ?? DefaultTemplate;
                default:
                    return DefaultTemplate;
            }
        }
    }
}
