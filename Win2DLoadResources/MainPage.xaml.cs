using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace Win2DLoadResources
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        public MainPage()
        {
            this.InitializeComponent();

            canvasAnimatedControl.CreateResources += CanvasAnimatedControl_CreateResources;
            canvasAnimatedControl.Update += CanvasAnimatedControl_Update;
            canvasAnimatedControl.Draw += CanvasAnimatedControl_Draw;
        }

        private string imageFilePath = @"Assets\StoreLogo.png";
        private CanvasBitmap canvasBitmap;

        private void CanvasAnimatedControl_CreateResources(Microsoft.Graphics.Canvas.UI.Xaml.CanvasAnimatedControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
        {
            args.TrackAsyncAction(CreateResourcesAsync(sender).AsAsyncAction());
        }

        private void CanvasAnimatedControl_Update(Microsoft.Graphics.Canvas.UI.Xaml.ICanvasAnimatedControl sender, Microsoft.Graphics.Canvas.UI.Xaml.CanvasAnimatedUpdateEventArgs args)
        {
            // Check if there is already an outstanding level-loading Task.
            // If so, don't try to spin up a new one.
            bool beginLoad = levelLoadTask == null && needToLoad;
            needToLoad = false;

            if (beginLoad)
            {
                levelLoadTask = LoadResourcesForLevelAsync(sender);
            }

            // Indicates the loading task was run and just finished.
            if (levelLoadTask != null && levelLoadTask.IsCompleted)
            {
                AggregateException levelLoadException = levelLoadTask.Exception;
                levelLoadTask = null;

                // Query the load task results and re-throw any exceptions
                // so Win2D can see them. This implements requirement #2.
                if (levelLoadException != null)
                {
                    // .NET async tasks wrap all errors in an AggregateException.
                    // We unpack this so Win2D can directly see any lost device errors.
                    levelLoadException.Handle(exception => { throw exception; });
                }
            }
        }

        private void CanvasAnimatedControl_Draw(Microsoft.Graphics.Canvas.UI.Xaml.ICanvasAnimatedControl sender, Microsoft.Graphics.Canvas.UI.Xaml.CanvasAnimatedDrawEventArgs args)
        {
            if (IsLoadInProgress())
            {
                args.DrawingSession.Clear(Colors.Red);
            }
            else
            {
                args.DrawingSession.Clear(Colors.Green);
                if (canvasBitmap != null)
                {
                    args.DrawingSession.DrawImage(canvasBitmap);
                }
            }
        }

        Task LoadResourcesForLevelAsync(ICanvasAnimatedControl canvasAnimatedControl)
        {
            return GameLoopSynchronizationContext.RunOnGameLoopThreadAsync(canvasAnimatedControl, async () =>
            {
                StorageFile imageFile = await Windows.ApplicationModel.Package.Current.InstalledLocation.GetFileAsync(imageFilePath);
                canvasBitmap = await CanvasBitmap.LoadAsync(canvasAnimatedControl, imageFile.Path);

                await Task.CompletedTask;
            });
        }

        // Shared state between all threads, and a lock to control access to it.
        bool needToLoad;
        Task levelLoadTask; // This implements requirement #1.

        void LoadNewLevel()
        {
            needToLoad = true;
        }

        async Task CreateResourcesAsync(CanvasAnimatedControl sender)
        {
            // If there is a previous load in progress, stop it, and
            // swallow any stale errors. This implements requirement #3.
            if (levelLoadTask != null)
            {
                levelLoadTask.AsAsyncAction().Cancel();
                try { await levelLoadTask; } catch { }
                levelLoadTask = null;
            }

            // Unload resources used by the previous level here.

            // If we are already in a level, reload its per-level resources.
            // This implements requirement #4.
            LoadNewLevel();
        }

        bool IsLoadInProgress()
        {
            return levelLoadTask != null;
        }
    }

    class GameLoopSynchronizationContext : SynchronizationContext
    {
        ICanvasAnimatedControl control;


        // Constructor.
        public GameLoopSynchronizationContext(ICanvasAnimatedControl control)
        {
            this.control = control;
        }


        // Posts a single atomic action for asynchronous execution on the game loop thread.
        public override void Post(SendOrPostCallback callback, object state)
        {
            var action = control.RunOnGameLoopThreadAsync(() =>
            {
                // Re-register ourselves as the current synchronization context,
                // to work around CLR issues where this state can sometimes get nulled out.
                SynchronizationContext.SetSynchronizationContext(this);

                callback(state);
            });
        }


        // Runs an action, which could contain an arbitrarily complex chain of async awaits,
        // on the game loop thread. This helper registers a custom synchronization context
        // to make sure every await continuation in the chain remains on the game loop
        // thread, regardless of which thread the lower level async operations complete on.
        // It wraps the entire chain with a TaskCompletionSource in order to return a single
        // Task that will be signalled only when the whole chain has completed.
        public static async Task RunOnGameLoopThreadAsync(ICanvasAnimatedControl control, Func<Task> callback)
        {
            var completedSignal = new TaskCompletionSource<object>();

            await control.RunOnGameLoopThreadAsync(async () =>
            {
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new GameLoopSynchronizationContext(control));

                    await callback();

                    completedSignal.SetResult(null);
                }
                catch (Exception e)
                {
                    completedSignal.SetException(e);
                }
            });

            await completedSignal.Task;
        }
    };
}