using System;
using System.Windows;

namespace TIAOpennessStudio.Services
{
    public partial class InputDialog : Window
    {
        public InputDialog(string title, string question, string defaultAnswer = "")
        {
            InitializeComponent();
            this.Title = title;
            lblQuestion.Text = question;
            txtAnswer.Text = defaultAnswer;
            
            // Focus text box
            Loaded += (s, e) => {
                 txtAnswer.Focus();
                 txtAnswer.SelectAll();
            };
        }

        public string Answer { get { return txtAnswer.Text; } }

        private void btnDialogOk_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true;
        }
    }
}