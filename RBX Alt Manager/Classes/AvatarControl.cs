using RestSharp;
using System;
using System.Windows.Forms;

namespace RBX_Alt_Manager.Classes
{
    public partial class AvatarControl : UserControl
    {
        private long OutfitID;

        public AvatarControl(string Name, long OutfitID)
        {
            this.OutfitID = OutfitID;

            InitializeComponent();
            AvatarName.Rescale();

            AvatarName.Text = Name;
        }

        private async void AvatarControl_Load(object sender, EventArgs e)
        {
            try
            {
                string imageUrl = await Batch.GetImage(OutfitID, "Outfit", "420x420");

                if (string.IsNullOrWhiteSpace(imageUrl)
                    || string.Equals(imageUrl, "UNK", StringComparison.OrdinalIgnoreCase)
                    || !Uri.TryCreate(imageUrl, UriKind.Absolute, out _))
                {
                    Program.Logger.Error($"Invalid outfit image URL for {OutfitID}: {imageUrl ?? "(null)"}");
                    return;
                }

                AvatarImage.LoadAsync(imageUrl);
            }
            catch (Exception ex)
            {
                Program.Logger.Error($"Failed loading outfit image for {OutfitID}: {ex}");
            }
        }

        private async void wearAvatarToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (AccountManager.SelectedAccount == null) return;

            var Request = new RestRequest($"v1/outfits/{OutfitID}/details", Method.Get);
            var Details = await AccountManager.AvatarClient.ExecuteAsync(Request);

            AccountManager.SelectedAccount.SetAvatar(Details.Content);
        }

        private async void copyAvatarJSONToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var Request = new RestRequest($"v1/outfits/{OutfitID}/details", Method.Get);
            var Details = await AccountManager.AvatarClient.ExecuteAsync(Request);

            Clipboard.SetText(Details.Content);
        }
    }
}
