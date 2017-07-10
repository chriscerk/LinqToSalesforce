﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Xml.Serialization;
using EnvDTE;
using LinqToSalesforce.VsPlugin2017.Ioc;
using LinqToSalesforce.VsPlugin2017.Model;
using LinqToSalesforce.VsPlugin2017.Storage;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Control;
using Microsoft.FSharp.Core;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.TextManager.Interop;
using Newtonsoft.Json;
using Task = System.Threading.Tasks.Task;

namespace LinqToSalesforce.VsPlugin2017.Ui
{
    /// <summary>
    /// Interaction logic for AuthenticationControl.xaml
    /// </summary>
    public partial class AuthenticationControl : Page
    {
        private readonly DTE dte;
        readonly DiagramDocumentStorage documentStorage = new DiagramDocumentStorage();
        private DiagramDocument document;

        public AuthenticationControl(string filename, DTE dte)
        {
            this.dte = dte;
            Filename = filename;

            InitializeComponent();
        }
        
        public string Filename { get; }

        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);

            document = documentStorage.LoadDocument(Filename);
            if (document?.Credentials != null)
            {
                ClientIdBox.Text = document.Credentials.ClientId;
                ClientSecretBox.Text = document.Credentials.ClientSecret;
                UsernameBox.Text = document.Credentials.Username;
                PasswordEntry.Password = document.Credentials.Password;
                SecurityTokenBox.Text = document.Credentials.SecurityToken;
                InstanceBox.Text = document.Credentials.Instance;

                CheckCredentials();
            }
        }

        private void CheckCredentials()
        {
            Task.Factory.StartNew(async () =>
            {
                var identity = await AuthenticateAsync();
                if (string.IsNullOrWhiteSpace(identity?.AccessToken))
                    MessageBox.Show("Authentication failed !");
                else
                {
                    IocServiceProvider.Current.Identity = identity;
                    Dispatcher.Invoke(() => DisplayTablesSelector(identity));
                }
            });
        }

        
        private void DisplayTablesSelector(Rest.OAuth.Identity identity)
        {
            var tablesSelectorControl = new TablesSelectorControl();
            ////tablesSelectorControl.Backclicked += (sender, args) => { Content = MainGrid; };
            //Content = tablesSelectorControl;

            NavigationService.GetNavigationService(this).Navigate(tablesSelectorControl);
        }

        public async Task<Rest.OAuth.Identity> AuthenticateAsync()
        {
            try
            {
                var param = document.Credentials.ToImpersonationParam();
                var authenticateWithCredentials = Rest.OAuth.authenticateWithCredentials(param);

                return FSharpAsync.RunSynchronously(authenticateWithCredentials, FSharpOption<int>.None,
                    FSharpOption<CancellationToken>.None);
            }
            catch
            {
                return null;
            }
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null)
                return;
            
            Rest.Config.ProductionInstance = InstanceBox.Text;
            var oauth = new Rest.OAuth.ImpersonationParam
            {
                ClientId = ClientIdBox.Text,
                ClientSecret = ClientSecretBox.Text,
                Username = UsernameBox.Text,
                Password = PasswordEntry.Password,
                SecurityToken = SecurityTokenBox.Text
            };

            if (document == null)
                document = new DiagramDocument();

            document.Credentials = Credentials.From(oauth);
            document.Credentials.Instance = InstanceBox.Text;

            documentStorage.Save(document, Filename);

            CheckCredentials();
        }

        private void Button_Click_1(object sender, RoutedEventArgs e)
        {
            VsShellUtilities.OpenBrowser("https://rflechner.github.io/LinqToSalesforce/getting_started_with_salesforce.html");
        }
    }

}
