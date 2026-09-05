using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using AudioBoarder.App;
using AudioBoarder.App.Configuration;
using AudioBoarder.App.Setup;

namespace AudioBoarder.Tests.Setup;

public sealed class NativeAzureSetupTests
{
    [Fact]
    public void SwitchingBackToOriginalProfilePreservesItsUntouchedKey()
    {
        RunOnSta(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"setup-profile-ui-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                var service = new SettingsService(Path.Combine(root, "default.json"), Path.Combine(root, "user.json"));
                var original = new AudioBoarderSettings();
                original.AzureOpenAI.TenantId = "tenant-a";
                original.AzureOpenAI.Endpoint = "https://a.example/";
                original.AzureOpenAI.UseManagedIdentity = false;
                var a = new ModelAccountSettings { Id = "a", Name = "A" };
                a.CaptureFrom(original.AzureOpenAI, original.CloudTranscription, original.ImageGeneration);
                original.ModelAccounts.Add(a);
                original.ModelAccounts.Add(new ModelAccountSettings { Id = "b", Name = "B", Endpoint = "https://b.example/", TenantId = "tenant-b" });
                original.ActiveModelAccountId = a.Id;
                service.SaveAsync(original, new SettingsSecrets("test-api-key", null)).GetAwaiter().GetResult();
                var settings = new SettingsWindow(service, new LocalDataService(root), new DeclineDeletion(),
                    new SetupInventory(), new SetupCredentials());
                var profiles = (ComboBox)settings.FindName("ModelAccountCombo");

                profiles.SelectedIndex = 1;
                profiles.SelectedIndex = 0;

                ((AudioBoarderSettings)settings.DataContext).AzureOpenAI.ApiKey.Should().Be("test-api-key");
                settings.Close();
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });
    }

    [Fact]
    public void WizardAndSettingsConstructAsNativeWindowsWithValidTabs()
    {
        RunOnSta(() =>
        {
            var inventory = AzureSetupViewModelTests.ReadyInventory();
            var credentials = new SetupCredentials();
            using var vm = new AzureSetupViewModel(inventory, credentials, new AudioBoarderSettings());
            var wizard = new AzureSetupWindow(vm);
            wizard.Content.Should().BeOfType<Grid>();
            var tabs = (TabControl)wizard.FindName("Steps");
            tabs.Items.Count.Should().Be(3);
            tabs.SelectedIndex = 1;
            tabs.SelectedIndex = 2;
            wizard.Close();

            var provisioning = new AzureProvisioningWindow(AzureProvisioningViewModelTests.Create(new ProvisioningFake()));
            provisioning.Content.Should().BeOfType<Grid>();
            provisioning.Close();

            var root = Path.Combine(Path.GetTempPath(), $"unused-setup-ui-{Guid.NewGuid():N}");
            var settings = new SettingsWindow(
                new SettingsService(Path.Combine(root, "default.json"), Path.Combine(root, "user.json")),
                new LocalDataService(root), new DeclineDeletion(), inventory, credentials);
            settings.ShowAzureSection();
            ((TabItem)settings.FindName("AzureTab")).IsSelected.Should().BeTrue();
            settings.Content.Should().BeOfType<Grid>();
            settings.Close();
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(20)).Should().BeTrue("native window initialization should not block");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class DeclineDeletion : ILocalDataDeletionConfirmation
    {
        public bool ConfirmDeleteLocalData() => false;
    }
}
