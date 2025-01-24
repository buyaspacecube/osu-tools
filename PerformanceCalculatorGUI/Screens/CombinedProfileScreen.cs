// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Input.Events;
using osu.Framework.Logging;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Overlays;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osuTK.Graphics;
using osuTK.Input;
using PerformanceCalculatorGUI.Components;
using PerformanceCalculatorGUI.Components.TextBoxes;
using PerformanceCalculatorGUI.Configuration;
using ButtonState = PerformanceCalculatorGUI.Components.ButtonState;

namespace PerformanceCalculatorGUI.Screens
{
    public partial class CombinedProfileScreen : PerformanceCalculatorScreen
    {
        [Cached]
        private OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Plum);

        private StatefulButton calculationButton;
        private VerboseLoadingLayer loadingLayer;

        private GridContainer layout;

        private FillFlowContainer<ExtendedProfileScore> scores;

        private LabelledTextBox usernameTextBox;
        private Container userPanelContainer;
        private UserCard userPanel;

        private string currentUser;

        private CancellationTokenSource calculationCancellatonToken;

        private OverlaySortTabControl<ProfileSortCriteria> sortingTabControl;
        private readonly Bindable<ProfileSortCriteria> sorting = new Bindable<ProfileSortCriteria>(ProfileSortCriteria.Local);

        [Resolved]
        private NotificationDisplay notificationDisplay { get; set; }

        [Resolved]
        private APIManager apiManager { get; set; }

        [Resolved]
        private Bindable<RulesetInfo> ruleset { get; set; }

        [Resolved]
        private SettingsManager configManager { get; set; }

        [Resolved]
        private RulesetStore rulesets { get; set; }

        public override bool ShouldShowConfirmationDialogOnSwitch => false;

        private const float username_container_height = 40;

        public CombinedProfileScreen()
        {
            RelativeSizeAxes = Axes.Both;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            InternalChildren = new Drawable[]
            {
                layout = new GridContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    ColumnDimensions = new[] { new Dimension() },
                    RowDimensions = new[]
                    {
                        new Dimension(GridSizeMode.Absolute, username_container_height),
                        new Dimension(GridSizeMode.Absolute),
                        new Dimension(GridSizeMode.AutoSize),
                        new Dimension()
                    },
                    Content = new[]
                    {
                        new Drawable[]
                        {
                            new GridContainer
                            {
                                Name = "Settings",
                                Height = username_container_height,
                                RelativeSizeAxes = Axes.X,
                                ColumnDimensions = new[]
                                {
                                    new Dimension(),
                                    new Dimension(GridSizeMode.AutoSize)
                                },
                                RowDimensions = new[]
                                {
                                    new Dimension(GridSizeMode.AutoSize)
                                },
                                Content = new[]
                                {
                                    new Drawable[]
                                    {
                                        usernameTextBox = new ExtendedLabelledTextBox
                                        {
                                            RelativeSizeAxes = Axes.X,
                                            Anchor = Anchor.TopLeft,
                                            Label = "Usernames",
                                            PlaceholderText = "Fortnite, Marques Brownlee",
                                            CommitOnFocusLoss = false
                                        },
                                        calculationButton = new StatefulButton("Start calculation")
                                        {
                                            Width = 150,
                                            Height = username_container_height,
                                            Action = () => { calculateCombinedProfile(usernameTextBox.Current.Value); }
                                        }
                                    }
                                }
                            },
                        },
                        new Drawable[]
                        {
                            userPanelContainer = new Container
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y
                            }
                        },
                        new Drawable[]
                        {
                            new Container
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Children = new Drawable[]
                                {
                                    sortingTabControl = new OverlaySortTabControl<ProfileSortCriteria>
                                    {
                                        Anchor = Anchor.CentreRight,
                                        Origin = Anchor.CentreRight,
                                        Margin = new MarginPadding { Right = 22 },
                                        Current = { BindTarget = sorting },
                                        Alpha = 0
                                    }
                                }
                            }
                        },
                        new Drawable[]
                        {
                            new OsuScrollContainer(Direction.Vertical)
                            {
                                RelativeSizeAxes = Axes.Both,
                                Child = scores = new FillFlowContainer<ExtendedProfileScore>
                                {
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                    Direction = FillDirection.Vertical
                                }
                            }
                        },
                    }
                },
                loadingLayer = new VerboseLoadingLayer(true)
                {
                    RelativeSizeAxes = Axes.Both
                }
            };

            usernameTextBox.OnCommit += (_, _) => { calculateCombinedProfile(usernameTextBox.Current.Value); };
            sorting.ValueChanged += e => { updateSorting(e.NewValue); };

            if (RuntimeInfo.IsDesktop)
                HotReloadCallbackReceiver.CompilationFinished += _ => Schedule(() => { calculateCombinedProfile(currentUser); });
        }

        private void calculateCombinedProfile(string usernames)
        {
			string[] usernamesArray = usernames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			
            if (usernamesArray.Length < 1)
            {
                usernameTextBox.FlashColour(Color4.Red, 1);
                return;
            }

            calculationCancellatonToken?.Cancel();
            calculationCancellatonToken?.Dispose();

            loadingLayer.Show();
            calculationButton.State.Value = ButtonState.Loading;

            scores.Clear();

            calculationCancellatonToken = new CancellationTokenSource();
            var token = calculationCancellatonToken.Token;

            Task.Run(async () =>
            {
				var plays = new List<ExtendedScore>();
				int totalPlaycount = 0;
				
				foreach (string username in usernamesArray)
				{
					Schedule(() => loadingLayer.Text.Value = "Getting user data...");

					var player = await apiManager.GetJsonFromApi<APIUser>($"users/{username}/{ruleset.Value.ShortName}");

					currentUser = player.Username;

					Schedule(() =>
					{
						if (userPanel != null)
							userPanelContainer.Remove(userPanel, true);

						userPanelContainer.Add(userPanel = new UserCard(player)
						{
							RelativeSizeAxes = Axes.X
						});

						sortingTabControl.Alpha = 1.0f;
						sortingTabControl.Current.Value = ProfileSortCriteria.Local;

						layout.RowDimensions = new[]
						{
							new Dimension(GridSizeMode.Absolute, username_container_height),
							new Dimension(GridSizeMode.AutoSize),
							new Dimension(GridSizeMode.AutoSize),
							new Dimension()
						};
					});

					if (token.IsCancellationRequested)
						return;

					var rulesetInstance = ruleset.Value.CreateInstance();

					Schedule(() => loadingLayer.Text.Value = $"Calculating {player.Username} top scores...");

					var apiScores = await apiManager.GetJsonFromApi<List<SoloScoreInfo>>($"users/{player.OnlineID}/scores/best?mode={ruleset.Value.ShortName}&limit=100");

					foreach (var score in apiScores)
					{
						if (token.IsCancellationRequested)
							return;

						var working = ProcessorWorkingBeatmap.FromFileOrId(score.BeatmapID.ToString(), cachePath: configManager.GetBindable<string>(Settings.CachePath).Value);

						Schedule(() => loadingLayer.Text.Value = $"Calculating {working.Metadata}");

						Mod[] mods = score.Mods.Select(x => x.ToMod(rulesetInstance)).ToArray();

						var scoreInfo = score.ToScoreInfo(rulesets, working.BeatmapInfo);

						var parsedScore = new ProcessorScoreDecoder(working).Parse(scoreInfo);

						var difficultyCalculator = rulesetInstance.CreateDifficultyCalculator(working);
						var difficultyAttributes = difficultyCalculator.Calculate(RulesetHelper.ConvertToLegacyDifficultyAdjustmentMods(rulesetInstance, mods));
						var performanceCalculator = rulesetInstance.CreatePerformanceCalculator();

						var livePp = score.PP ?? 0.0;
						var perfAttributes = await performanceCalculator?.CalculateAsync(parsedScore.ScoreInfo, difficultyAttributes, token)!;
						score.PP = perfAttributes?.Total ?? 0.0;

						var extendedScore = new ExtendedScore(score, livePp, perfAttributes, player);
						plays.Add(extendedScore);
					}
					
					totalPlaycount += player.BeatmapPlayCountsCount;
				}
				
				Schedule(() => loadingLayer.Text.Value = "Filtering scores");
				
				var filteredPlays = new List<ExtendedScore>();
				
				List<int> beatmapIDs = plays.Select(x => x.SoloScore.BeatmapID).Distinct().ToList();
				
				foreach (int beatmapID in beatmapIDs)
				{
					List<ExtendedScore> playsOnBeatmap = plays.Where(x => x.SoloScore.BeatmapID == beatmapID).OrderByDescending(x => x.SoloScore.PP).ToList();
					ExtendedScore bestPlayOnBeatmap = playsOnBeatmap.First();
					
					filteredPlays.Add(bestPlayOnBeatmap);
					Schedule(() => scores.Add(new ExtendedProfileScore(bestPlayOnBeatmap)));
				}
				plays = filteredPlays;
				
				if (token.IsCancellationRequested)
					return;

				var localOrdered = plays.OrderByDescending(x => x.SoloScore.PP).ToList();
				var liveOrdered = plays.OrderByDescending(x => x.LivePP).ToList();

				Schedule(() =>
				{
					foreach (var play in plays)
					{
						play.Position.Value = localOrdered.IndexOf(play) + 1;
						play.PositionChange.Value = liveOrdered.IndexOf(play) - localOrdered.IndexOf(play);
						scores.SetLayoutPosition(scores[liveOrdered.IndexOf(play)], localOrdered.IndexOf(play));
					}
				});

				decimal totalLocalPP = 0;
				for (var i = 0; i < Math.Min(localOrdered.Count, 100); i++)
					totalLocalPP += (decimal)(Math.Pow(0.95, i) * (localOrdered[i].SoloScore.PP ?? 0));

				decimal totalLivePP = 0;
				for (var i = 0; i < Math.Min(liveOrdered.Count, 100); i++)
					totalLivePP += (decimal)(Math.Pow(0.95, i) * liveOrdered[i].LivePP);
				
				decimal playcountPP = (decimal)(416.6667 * 1.0 - Math.Pow(0.995, totalPlaycount));

				Schedule(() =>
				{
					userPanel.Data.Value = new UserCardData
					{
						LivePP = totalLivePP + playcountPP,
						LocalPP = totalLocalPP + playcountPP,
						PlaycountPP = playcountPP
					};
				});
            }, token).ContinueWith(t =>
            {
                Logger.Log(t.Exception?.ToString(), level: LogLevel.Error);
                notificationDisplay.Display(new Notification(t.Exception?.Flatten().Message));
            }, TaskContinuationOptions.OnlyOnFaulted).ContinueWith(t =>
            {
                Schedule(() =>
                {
                    loadingLayer.Hide();
                    calculationButton.State.Value = ButtonState.Done;
                });
            }, TaskContinuationOptions.None);
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            calculationCancellatonToken?.Cancel();
            calculationCancellatonToken?.Dispose();
            calculationCancellatonToken = null;
        }

        protected override bool OnKeyDown(KeyDownEvent e)
        {
            if (e.Key == Key.Escape && !calculationCancellatonToken.IsCancellationRequested)
            {
                calculationCancellatonToken?.Cancel();
            }

            return base.OnKeyDown(e);
        }

        private void updateSorting(ProfileSortCriteria sortCriteria)
        {
            if (!scores.Children.Any())
                return;

            ExtendedProfileScore[] sortedScores;

            switch (sortCriteria)
            {
                case ProfileSortCriteria.Live:
                    sortedScores = scores.Children.OrderByDescending(x => x.Score.LivePP).ToArray();
                    break;

                case ProfileSortCriteria.Local:
                    sortedScores = scores.Children.OrderByDescending(x => x.Score.PerformanceAttributes.Total).ToArray();
                    break;

                case ProfileSortCriteria.Difference:
                    sortedScores = scores.Children.OrderByDescending(x => x.Score.PerformanceAttributes.Total - x.Score.LivePP).ToArray();
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(sortCriteria), sortCriteria, null);
            }

            for (int i = 0; i < sortedScores.Length; i++)
            {
                scores.SetLayoutPosition(sortedScores[i], i);
            }
        }
    }
}
