namespace TIAOpennessStudio.ViewModels
{
    using System.Collections.Generic;

    public class OneStep
    {
        public int Number { get; set; }
        public int NextStep { get; set; }
        public int AbortStep { get; set; }
        public int OptionStep { get; set; }
        public string Description { get; set; }
    }

    public class OneStepCollection
    {
        public List<OneStep> Steps { get; set; } = new List<OneStep>();
    }
}