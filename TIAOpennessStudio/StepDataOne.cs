using System.Collections.Generic;

namespace TIAOpennessStudio
{
    public class StepDataOne
    {
        // some fixed assignements:
        // step   1 is always the first step (initial)
        // step 100 is always a "step end"
        // we can work only step 1 to 100
        //
        // later this will come from the config file

        public List<OneStep> Steps = new List<OneStep>();

        // one step sequence for the first test
        public StepDataOne()
        {
            var one = new OneStep
            {
                Number = 1,
                Description = "Step 1",
                NextStep = 21,
                AbortStep = 0,
                OptionStep = 0,
                WaitingTime = 0,
                MonitorTime = 0
            };
            one.Number = 1;
            one.Description = "Step 1";
            one.NextStep = 21;
            one.AbortStep = 0;
            one.OptionStep = 0;

            one.WaitingTime = 0;
            one.MonitorTime = 0;
            Steps.Add(one);

            one = new OneStep
            {
                Number = 21,
                Description = "Step 21",
                NextStep = 27,
                AbortStep = 32,
                OptionStep = 43,
                WaitingTime = 0,
                MonitorTime = 0
            };
            Steps.Add(one);

            one = new OneStep
            {
                Number = 27,
                Description = "Step 27",
                NextStep = 32,
                AbortStep = 0,
                OptionStep = 0,
                WaitingTime = 0,
                MonitorTime = 0
            };
            Steps.Add(one);


            one = new OneStep
            {
                Number = 32,
                Description = "Step 32",
                NextStep = 43,
                AbortStep = 0,
                OptionStep = 0,
                WaitingTime = 0,
                MonitorTime = 0
            };

            Steps.Add(one);

            one = new OneStep
            {
                Number = 43,
                Description = "Step 43",
                NextStep = 100, // step seq. with end
                AbortStep = 0,
                OptionStep = 0,
                WaitingTime = 0,
                MonitorTime = 0
            };
            Steps.Add(one);

            one = new OneStep
            {
                Number = 43,
                Description = "Step 43",
                NextStep = 100, // step seq. with end
                AbortStep = 0,
                OptionStep = 0,
                WaitingTime = 0,
                MonitorTime = 0
            };
            Steps.Add(one);
        }

       
    }

    public class OneStep
    {
        public int Number;
        public string Description;
        public int NextStep;
        public int AbortStep;
        public int OptionStep;

        public int WaitingTime;
        public int MonitorTime;

    }
}
