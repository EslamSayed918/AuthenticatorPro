using System;
using System.Linq;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using AndroidX.Work;
using AuthenticatorPro.Droid.Activity;
using AuthenticatorPro.Droid.Util;
using AuthenticatorPro.Droid.Worker;
using Google.Android.Material.Button;
using Google.Android.Material.SwitchMaterial;
using Java.Util.Concurrent;
using Xamarin.Essentials;
using Uri = Android.Net.Uri;

namespace AuthenticatorPro.Droid.Fragment
{
    internal class AutoBackupSetupBottomSheet : BottomSheet
    {
        private const int RequestPicker = 0;
        private PreferenceWrapper _preferences;

        private TextView _locationStatusText;
        private TextView _passwordStatusText;

        private SwitchMaterial _backupEnabledSwitch;
        private SwitchMaterial _restoreEnabledSwitch;
        private MaterialButton _backupNowButton;
        private MaterialButton _restoreNowButton;
        private LinearLayout _batOptimLayout;
        private MaterialButton _okButton;

        public AutoBackupSetupBottomSheet()
        {
            RetainInstance = true;
        }

        public override void OnActivityResult(int requestCode, int resultCode, Intent intent)
        {
            base.OnActivityResult(requestCode, resultCode, intent);

            if(requestCode != RequestPicker || (Result) resultCode != Result.Ok)
                return;
            
            OnLocationSelected(intent);
        }

        public override View OnCreateView(LayoutInflater inflater, ViewGroup container, Bundle savedInstanceState)
        {
            var view = inflater.Inflate(Resource.Layout.sheetAutoBackupSetup, null);
            SetupToolbar(view, Resource.String.prefAutoBackupTitle, true);

            _preferences = new PreferenceWrapper(Context);

            var selectLocationButton = view.FindViewById<LinearLayout>(Resource.Id.buttonSelectLocation);
            selectLocationButton.Click += OnSelectLocationClick;

            var setPasswordButton = view.FindViewById<LinearLayout>(Resource.Id.buttonSetPassword);
            setPasswordButton.Click += OnSetPasswordButtonClick;

            _locationStatusText = view.FindViewById<TextView>(Resource.Id.textLocationStatus);
            _passwordStatusText = view.FindViewById<TextView>(Resource.Id.textPasswordStatus);

            _backupNowButton = view.FindViewById<MaterialButton>(Resource.Id.buttonBackupNow);
            _backupNowButton.Click += OnBackupNowButtonClick;
            
            _restoreNowButton = view.FindViewById<MaterialButton>(Resource.Id.buttonRestoreNow);
            _restoreNowButton.Click += OnRestoreNowButtonClick;

            _batOptimLayout = view.FindViewById<LinearLayout>(Resource.Id.layoutBatOptim);
            var disableBatOptimButton = view.FindViewById<MaterialButton>(Resource.Id.buttonDisableBatOptim);
            disableBatOptimButton.Click += OnDisableBatOptimButtonClick;

            _okButton = view.FindViewById<MaterialButton>(Resource.Id.buttonOk);
            _okButton.Click += delegate { Dismiss(); };

            _backupEnabledSwitch = view.FindViewById<SwitchMaterial>(Resource.Id.switchBackupEnabled);
            _restoreEnabledSwitch = view.FindViewById<SwitchMaterial>(Resource.Id.switchRestoreEnabled);

            UpdateLocationStatusText();
            UpdatePasswordStatusText();
            UpdateSwitchesAndTriggerButton();
            
            return view;
        }

        public override void OnResume()
        {
            base.OnResume();
            
            if(Build.VERSION.SdkInt < BuildVersionCodes.M)
                return;
            
            var powerManager = (PowerManager) Context.GetSystemService(Context.PowerService);

            _batOptimLayout.Visibility = powerManager.IsIgnoringBatteryOptimizations(Context.PackageName)
                ? ViewStates.Gone
                : ViewStates.Visible;
        }

        public override void OnDismiss(IDialogInterface dialog)
        {
            base.OnDismiss(dialog);

            _preferences.AutoBackupEnabled = _backupEnabledSwitch.Checked;
            _preferences.AutoRestoreEnabled = _restoreEnabledSwitch.Checked;
            
            var shouldBeEnabled = _backupEnabledSwitch.Checked || _restoreEnabledSwitch.Checked;
            var workManager = WorkManager.GetInstance(Context);

            if(shouldBeEnabled)
            {
                var workRequest = new PeriodicWorkRequest.Builder(typeof(AutoBackupWorker), 15, TimeUnit.Minutes).Build();
                workManager.EnqueueUniquePeriodicWork(AutoBackupWorker.Name, ExistingPeriodicWorkPolicy.Keep, workRequest);
            }
            else
                workManager.CancelUniqueWork(AutoBackupWorker.Name);
        }

        private void OnBackupNowButtonClick(object sender, EventArgs e)
        {
            _preferences.AutoBackupTrigger = true;
            TriggerWork();
            Toast.MakeText(Context, Resource.String.backupScheduled, ToastLength.Short).Show();
        }
        
        private void OnRestoreNowButtonClick(object sender, EventArgs e)
        {
            _preferences.AutoRestoreTrigger = true;
            TriggerWork();
            Toast.MakeText(Context, Resource.String.restoreScheduled, ToastLength.Short).Show();
        }

        private void TriggerWork()
        {
            var request = new OneTimeWorkRequest.Builder(typeof(AutoBackupWorker)).Build();
            var manager = WorkManager.GetInstance(Context);
            manager.EnqueueUniqueWork(AutoBackupWorker.Name, ExistingWorkPolicy.Replace, request);
        }

        private void OnSelectLocationClick(object sender, EventArgs e)
        {
            var intent = new Intent(Intent.ActionOpenDocumentTree);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantPersistableUriPermission | ActivityFlags.GrantPrefixUriPermission);

            if(_preferences.AutoBackupUri != null)
                intent.PutExtra(DocumentsContract.ExtraInitialUri, _preferences.AutoBackupUri);

            ((SettingsActivity) Context).BaseApplication.PreventNextLock = true;
            StartActivityForResult(intent, RequestPicker);
        }
        
        private void OnSetPasswordButtonClick(object sender, EventArgs e)
        {
            var fragment = new BackupPasswordBottomSheet(BackupPasswordBottomSheet.Mode.Set);
            fragment.PasswordEntered += OnPasswordEntered;
            
            var activity = (SettingsActivity) Context;
            fragment.Show(activity.SupportFragmentManager, fragment.Tag);
        }

        private async void OnPasswordEntered(object sender, string password)
        {
            _preferences.AutoBackupPasswordProtected = password != "";
            ((BackupPasswordBottomSheet) sender).Dismiss();
            UpdatePasswordStatusText();
            UpdateSwitchesAndTriggerButton();
            await SecureStorageWrapper.SetAutoBackupPassword(password);
        }

        private void OnLocationSelected(Intent intent)
        {
            _preferences.AutoBackupUri = intent.Data;

            var flags = intent.Flags & (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
            Context.ContentResolver.TakePersistableUriPermission(intent.Data, flags);
            
            UpdateLocationStatusText();
            UpdateSwitchesAndTriggerButton();
        }

        private void UpdateLocationStatusText()
        {
            if(_preferences.AutoBackupUri == null)
            {
                _locationStatusText.SetText(Resource.String.noLocationSelected);
                return;
            }

            var location = _preferences.AutoBackupUri.LastPathSegment?.Split(':', 2).Last();
            _locationStatusText.Text = String.Format(GetString(Resource.String.locationSetTo), location ?? String.Empty);
        }

        private void UpdatePasswordStatusText()
        {
            _passwordStatusText.SetText(_preferences.AutoBackupPasswordProtected switch
            {
                null => Resource.String.passwordNotSet,
                false => Resource.String.notPasswordProtected,
                true => Resource.String.passwordSet
            });
        }

        private void UpdateSwitchesAndTriggerButton()
        {
            _backupEnabledSwitch.Checked = _preferences.AutoBackupEnabled;
            _restoreEnabledSwitch.Checked = _preferences.AutoRestoreEnabled;
           
            var canBeChecked = _preferences.AutoBackupUri != null && _preferences.AutoBackupPasswordProtected != null;
            _backupEnabledSwitch.Enabled = _restoreEnabledSwitch.Enabled = _backupNowButton.Enabled = _restoreNowButton.Enabled = canBeChecked;

            if(!canBeChecked)
                _backupEnabledSwitch.Checked = _restoreEnabledSwitch.Checked = false;
        }

        private void OnDisableBatOptimButtonClick(object sender, EventArgs e)
        {
            var intent = new Intent(Settings.ActionRequestIgnoreBatteryOptimizations);
            intent.SetData(Uri.Parse($"package:{Context.PackageName}"));
            StartActivity(intent);
        }
    }
}