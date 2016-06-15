﻿using System;
using System.Threading;
using System.Threading.Tasks;
using AcManager.Controls.Pages.Dialogs;
using AcManager.Tools.Helpers;
using AcManager.Tools.Helpers.Api.Rsr;
using FirstFloor.ModernUI.Helpers;

namespace AcManager.Tools.SpecialDriveModes {
    public class RsrStarter {
        private readonly int _eventId;

        public RsrStarter(int eventId) {
            _eventId = eventId;
        }

        public async Task Start(IProgress<string> progress = null, CancellationToken cancellation = default(CancellationToken)) {
            progress?.Report("Getting information about the event…");
            var entry = await RsrApiProvider.GetEventInformationAsync(_eventId, cancellation);
            Logging.Write("[RsrStarter] Car ID: " + entry?.CarId);
        }

        public void StartWithDialog() {
            using (var waiting = new WaitingDialog()) {
                Start(waiting, waiting.CancellationToken).Forget();
            }
        }
    }
}