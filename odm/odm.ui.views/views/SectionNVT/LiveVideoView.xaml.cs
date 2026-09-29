using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Collections.Specialized;
using System.Reactive.Disposables;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.FSharp.Control;
using Microsoft.Practices.Unity;
using odm.infra;
using odm.core;
using odm.player;
using odm.ui.controls;
using odm.ui.core;
using odm.ui.views;
using onvif.services;
using utils;

namespace odm.ui.activities {
	public partial class LiveVideoView : BasePropertyControl, IDisposable, IPlaybackController {

		#region Activity definition
		public static FSharpAsync<Result> Show(IUnityContainer container, Model model) {
			return container.StartViewActivity<Result>(context => {
				var view = new LiveVideoView(model, context);
				var presenter = container.Resolve<IViewPresenter>();
				presenter.ShowView(view);
			});
		}
		#endregion

		private CompositeDisposable disposables = new CompositeDisposable();
		private Model model;

		// The profile that plays. The selector at the top can change it.
		string currentProfToken;
		VideoResolution currentResolution;
		readonly SerialDisposable playerSubscription = new SerialDisposable();
		readonly SerialDisposable uriSubscription = new SerialDisposable();
		readonly SerialDisposable profilesSubscription = new SerialDisposable();

		private void Init(Model model) {
			OnCompleted += () => {
				disposables.Dispose();
			};
			this.model = model;
			currentProfToken = model.profToken;
			currentResolution = model.encoderResolution;
			disposables.Add(playerSubscription);
			disposables.Add(uriSubscription);
			disposables.Add(profilesSubscription);
			InitializeComponent();
            InitAnnоtation(model.encoderResolution);
			VideoStartup(model.profToken);
			ShowStreamUri(model.profToken);
			LoadProfiles();
		}

        private void InitAnnоtation(VideoResolution resolution)
        {
            // Use the current resolution, so that the overlay follows a profile change.
            Func<double, double> scaleX = (x) => (1 + x) * currentResolution.width / 2.0;
            Func<double, double> scaleY = (y) => (1 - y) * currentResolution.height / 2.0;

            { // objects
                objects.Width = resolution.width;
                objects.Height = resolution.height;

                movingObjectsHolder = new VAEntitiesHolder<VAObject, VAObjectSnapshot>(scaleX, scaleY, null);

                var binding = new Binding("Objects") { ElementName = root.Name };
                objects.SetBinding(ItemsControl.ItemsSourceProperty, binding);
            }

            { // alarms
                alarms.Width = resolution.width;
                alarms.Height = resolution.height;

                ((INotifyCollectionChanged)alarms.Items).CollectionChanged += (s, e) =>
                {
                    if (e.NewItems != null && e.NewItems.Count > 0)
                    {
                        var c = alarms.ItemContainerGenerator.ContainerFromItem(e.NewItems[e.NewItems.Count - 1]);
                        if (c is FrameworkElement)
                            ((FrameworkElement)c).BringIntoView();
                    }
                };

                alarmsHolder = new VAEntitiesHolder<VAAlarm, VAEntitySnapshot>(scaleX, scaleY, null);
                var binding = new Binding("Alarms") { ElementName = root.Name };
                alarms.SetBinding(ItemsControl.ItemsSourceProperty, binding);
            }
        }

		IPlaybackSession playbackSession;

        VAEntitiesHolder<VAObject, VAObjectSnapshot> movingObjectsHolder;
        public ObservableCollection<VAObject> Objects
        {
            get { return movingObjectsHolder.Entities; }
        }
        VAEntitiesHolder<VAAlarm, VAEntitySnapshot> alarmsHolder;
        public ObservableCollection<VAAlarm> Alarms
        {
            get { return alarmsHolder.Entities; }
        }

        StreamSetup CurrentStreamSetup()
        {
            return new StreamSetup() {
                stream = StreamType.rtpUnicast,
                transport = new Transport() {
                    protocol = AppDefaults.visualSettings.Transport_Type,
                    tunnel = null
                }
            };
        }

        void VideoStartup(string profToken)
        {
            var playerAct = activityContext.container.Resolve<IVideoPlayerActivity>();


            //subscribe to metadata
            IMetadataReceiver metadataReceiver = null;
            if (AppDefaults.visualSettings.EnableGraphicAnnotation)
            {
                var eventMetadataProcessor = new EventMetadataProcessor();
                eventMetadataProcessor.Processors.Add(new ObjectMotionMetadataProcessor(model.videoSourceToken, null, movingObjectsHolder.EntityInitialized, movingObjectsHolder.EntityChanged, movingObjectsHolder.EntityDeleted));
                eventMetadataProcessor.Processors.Add(new MotionAlarmMetadataProcessor(model.videoSourceToken, null, alarmsHolder.EntityInitialized, alarmsHolder.EntityChanged, alarmsHolder.EntityDeleted));
                eventMetadataProcessor.Processors.Add(new RegionMotionAlarmMetadataProcessor(model.videoSourceConfToken, model.videoAnalyticsConfToken, alarmsHolder.EntityInitialized, alarmsHolder.EntityChanged, alarmsHolder.EntityDeleted));
                eventMetadataProcessor.Processors.Add(new LoiteringAlarmMetadataProcessor(model.videoSourceConfToken, model.videoAnalyticsConfToken, alarmsHolder.EntityInitialized, alarmsHolder.EntityChanged, alarmsHolder.EntityDeleted));
                eventMetadataProcessor.Processors.Add(new AbandonedItemAlarmMetadataProcessor(model.videoSourceConfToken, model.videoAnalyticsConfToken, alarmsHolder.EntityInitialized, alarmsHolder.EntityChanged, alarmsHolder.EntityDeleted));
                eventMetadataProcessor.Processors.Add(new TripwireAlarmMetadataProcessor(model.videoSourceConfToken, model.videoAnalyticsConfToken, alarmsHolder.EntityInitialized, alarmsHolder.EntityChanged, alarmsHolder.EntityDeleted));
                eventMetadataProcessor.Processors.Add(new TamperingDetectorAlarmMetadataProcessor(model.videoSourceToken, null, alarmsHolder.EntityInitialized, alarmsHolder.EntityChanged, alarmsHolder.EntityDeleted));
                var metadataProcessor = new MetadataProcessor(eventMetadataProcessor, null);
                metadataReceiver = new MetadataFramer(metadataProcessor.Process);
            }

            var playerModel = new VideoPlayerActivityModel(
                profileToken: profToken,
                showStreamUrl: false,//TODO when true, annotation is not positioned correctly
                streamSetup: CurrentStreamSetup(),
                metadataReceiver: metadataReceiver
            );

            // Stop the previous stream before the new player starts.
            playerSubscription.Disposable = Disposable.Empty;
            playerSubscription.Disposable =
                activityContext.container.RunChildActivity(player, playerModel, (c, m) => playerAct.Run(c, m));
        }

        // Show the stream URI of the profile for the transport in the app settings.
        void ShowStreamUri(string profToken)
        {
            uriString.Text = "";
            var session = activityContext.container.Resolve<INvtSession>();
            uriSubscription.Disposable = session.GetStreamUri(CurrentStreamSetup(), profToken)
                .ObserveOnCurrentDispatcher()
                .Subscribe(mediaUri => {
                    uriString.Text = mediaUri != null && mediaUri.uri != null ? mediaUri.uri : "";
                }, err => {
                    dbg.Error(err);
                    uriString.Text = "stream URI unavailable: " + err.Message;
                });
        }

        /// <summary>One entry of the profile selector.</summary>
        public class ProfileItem {
            public string Token { get; set; }
            public string Text { get; set; }
            public VideoResolution Resolution { get; set; }
        }

        // List the profiles of this video source that have a video encoder.
        void LoadProfiles()
        {
            var session = activityContext.container.Resolve<INvtSession>();
            profilesSubscription.Disposable = session.GetProfiles()
                .ObserveOnCurrentDispatcher()
                .Subscribe(profiles => {
                    var playable = (profiles ?? new Profile[0])
                        .Where(p => p.videoEncoderConfiguration != null
                            && (model.videoSourceToken == null || p.videoSourceConfiguration == null
                                || p.videoSourceConfiguration.sourceToken == model.videoSourceToken))
                        .ToArray();
                    // Media2 gives the H265 encoding and the data that Media1 does not give.
                    profilesSubscription.Disposable = session.GetVideoEncoderConfigurationsMedia2()
                        .ObserveOnCurrentDispatcher()
                        .Subscribe(
                            media2 => FillProfiles(playable, media2),
                            err => { dbg.Error(err); FillProfiles(playable, null); });
                }, err => {
                    dbg.Error(err);
                });
        }

        static bool IsValid(VideoResolution r)
        {
            return r != null && r.width > 0 && r.height > 0;
        }

        void FillProfiles(Profile[] profiles, VideoEncoderConfiguration[] media2)
        {
            var items = profiles.Select(p => {
                var vec = p.videoEncoderConfiguration;
                var m2 = media2 == null ? null : media2.FirstOrDefault(c => c != null && c.token == vec.token);
                var encoding = m2 != null ? m2.encoding : vec.encoding;
                var res = IsValid(vec.resolution) ? vec.resolution : (m2 != null && IsValid(m2.resolution) ? m2.resolution : null);
                var name = string.IsNullOrEmpty(p.name) ? p.token : p.name;
                var details = encoding.ToString().ToUpperInvariant() + (res != null ? " " + res.width + "x" + res.height : "");
                return new ProfileItem { Token = p.token, Text = name + "  (" + details + ")", Resolution = res };
            }).ToList();

            profileSelector.SelectionChanged -= ProfileSelector_SelectionChanged;
            profileSelector.ItemsSource = items;
            profileSelector.SelectedItem = items.FirstOrDefault(i => i.Token == currentProfToken);
            profileSelector.IsEnabled = items.Count > 1;
            profileSelector.SelectionChanged += ProfileSelector_SelectionChanged;
        }

        void ProfileSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = profileSelector.SelectedItem as ProfileItem;
            if (item == null || item.Token == currentProfToken)
                return;
            currentProfToken = item.Token;
            if (item.Resolution != null) {
                currentResolution = item.Resolution;
                objects.Width = alarms.Width = item.Resolution.width;
                objects.Height = alarms.Height = item.Resolution.height;
            }
            VideoStartup(item.Token);
            ShowStreamUri(item.Token);
        }


		public void Dispose() {
			Cancel();
		}

		public void Shutdown() {
			
		}

		public new bool Initialized(IPlaybackSession playbackSession) {
			this.playbackSession = playbackSession;
			return true;
		}
	}
}
