﻿using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input.Touch;
using Microsoft.Xna.Framework.Audio;

namespace CocosSharp
{
    using XnaSurfaceFormat = Microsoft.Xna.Framework.Graphics.SurfaceFormat;
    using XnaMatrix = Microsoft.Xna.Framework.Matrix;

    public enum CCViewResolutionPolicy
    {
        Custom,         // Use ViewportRectRatio
        ExactFit,       // Fit to entire view. Distortion may occur
        NoBorder,       // Maintain design resolution aspect ratio, but scene may appear cropped
        ShowAll,        // Maintain design resolution aspect ratio, ensuring entire scene is visible
        FixedHeight,    // Use width of design resolution and scale height to aspect ratio of view
        FixedWidth      // Use height of design resolution and scale width to aspect ratio of view 

    }

    public partial class CCGameView: IDisposable
    {
        // (10 mill ticks per second / 60 fps) (rounded up)
        const int numOfTicksPerUpdate = 166667; 
        const int maxUpdateTimeMilliseconds = 500;

        static readonly CCRect exactFitViewportRatio = new CCRect(0,0,1,1);

        // Currently, we have a limitation that at most one view instance can be active at any point in time
        static WeakReference currentViewInstance;

        class CCGraphicsDeviceService : IGraphicsDeviceService
        {
            public GraphicsDevice GraphicsDevice { get; private set; }

            public CCGraphicsDeviceService(GraphicsDevice graphicsDevice)
            {
                GraphicsDevice = graphicsDevice;
            }

            public event EventHandler<EventArgs> DeviceCreated;
            public event EventHandler<EventArgs> DeviceDisposing;
            public event EventHandler<EventArgs> DeviceReset;
            public event EventHandler<EventArgs> DeviceResetting;
        }

        internal delegate void ViewportChangedEventHandler(CCGameView sender);
        internal event ViewportChangedEventHandler ViewportChanged;
        EventHandler<EventArgs> viewCreated;

        bool disposed;
        bool paused;
        bool viewInitialised;
        bool platformInitialised;
        bool gameLoaded;
        bool gameStarted;
        bool viewportDirty;

        CCViewResolutionPolicy resolutionPolicy = CCViewResolutionPolicy.ShowAll;
        CCRect viewportRatio = exactFitViewportRatio;
        CCSizeI designResolution = new CCSizeI(640, 480);

        Matrix defaultViewMatrix, defaultProjMatrix;
        Viewport defaultViewport;
        Viewport viewport;


        GraphicsDevice graphicsDevice;
        CCGraphicsDeviceService graphicsDeviceService;
        GameServiceContainer servicesContainer;

        GameTime gameTime;
        TimeSpan accumulatedElapsedTime;
        TimeSpan targetElapsedTime;
        TimeSpan maxElapsedTime;
        Stopwatch gameTimer;
        long previousTicks;


        #region Properties

        public event EventHandler<EventArgs> ViewCreated 
        { 
            add { viewCreated += value; LoadGame(); }
            remove { viewCreated -= value; }
        }


        // Instance properties

        public CCDirector Director { get; private set; }
        public CCScheduler Scheduler { get; private set; }
        public CCRenderer Renderer { get { return DrawManager != null ? DrawManager.Renderer : null; } }
        public CCAudioEngine AudioEngine { get; private set; }
        public CCActionManager ActionManager { get; private set; }

        public CCContentManager ContentManager { get; private set; }
        internal CCFontAtlasCache FontAtlasCache { get; private set; }
        public CCSpriteFontCache SpriteFontCache { get; private set; }
        public CCSpriteFrameCache SpriteFrameCache { get; private set; }
        public CCParticleSystemCache ParticleSystemCache { get; private set; }
        public CCTextureCache TextureCache { get; private set; }
        public CCAnimationCache AnimationCache { get; private set; }

        public CCStats Stats { get; private set; }

        public bool DepthTesting
        {
            get { return Renderer.UsingDepthTest; }
            set { Renderer.UsingDepthTest = value; }
        }

        public bool Paused
        {
            get { return paused; }
            set 
            {
                if (gameStarted && paused != value)
                {
                    paused = value;
                    if (gameTimer != null) 
                    {
                        previousTicks = gameTimer.Elapsed.Ticks;
                    }

                    if (paused) 
                    {
                        AudioEngine?.PauseBackgroundMusic();
                        AudioEngine?.PauseAllEffects();
                    } 
                    else 
                    {
                        AudioEngine?.ResumeBackgroundMusic();
                        AudioEngine?.ResumeAllEffects();
                    }
                    
                    PlatformUpdatePaused();
                }
            }
        }

        public CCViewResolutionPolicy ResolutionPolicy 
        { 
            get { return resolutionPolicy; }
            set 
            {
                resolutionPolicy = value;

                // Reset ratio if using custom resolution policy
                if(resolutionPolicy == CCViewResolutionPolicy.Custom)
                    viewportRatio = exactFitViewportRatio;
                viewportDirty = true;
            }
        }

        public CCBlendFunc BlendType
        {
            set { DrawManager.BlendFunc(value); }
        }

        public CCSizeI DesignResolution
        {
            get { return designResolution; }
            set
            {
                designResolution = value;
                viewportDirty = true;
            }
        }

        public CCSizeI ViewSize
        {
            get; private set;
        }

        public CCRect ViewportRectRatio
        {
            get { return viewportRatio; }
            set 
            {
                viewportRatio = value;
                resolutionPolicy = CCViewResolutionPolicy.Custom;
                viewportDirty = true;
            }
        }

        internal CCEventDispatcher EventDispatcher { get; private set; }
        internal CCDrawManager DrawManager { get; private set; }

        internal Viewport Viewport 
        {
            get 
            { 
                if(viewportDirty) 
                    UpdateViewport(); 
                return viewport; 
            }
            private set 
            {
                viewport = value;
                if(ViewportChanged != null)
                    ViewportChanged(this);
            }
        }

        #endregion Properties


        #region Initialisation

        public void StartGame()
        {
            if(!gameStarted)
            {
                PlatformStartGame();
                gameStarted = true;
            }
        }

        public void RunWithScene(CCScene scene)
        {
            StartGame();
            Director.RunWithScene(scene);
        }
            
        void Initialise()
        {
            if (viewInitialised)
                return;

            PlatformInitialise();

            ActionManager = new CCActionManager();
            Director = new CCDirector();
            EventDispatcher = new CCEventDispatcher(this);
            AudioEngine = new CCAudioEngine();
            Scheduler = new CCScheduler();

            // Latest instance of game view should be setting shared resources
            // Ideally, we should move towards removing these singletons altogetehr
            CCAudioEngine.SharedEngine = AudioEngine;
            CCScheduler.SharedScheduler = Scheduler;

            Stats = new CCStats();

            InitialiseGraphicsDevice();

            InitialiseResourceCaches();

            InitialiseRunLoop();

            InitialiseInputHandling();

            Stats.Initialise();

            viewInitialised = true;

            currentViewInstance = new WeakReference(this);
        }

        void InitialiseGraphicsDevice()
        {
            var presParams = new PresentationParameters();
            presParams.RenderTargetUsage = RenderTargetUsage.PreserveContents;
            presParams.DepthStencilFormat = DepthFormat.Depth24Stencil8;
            presParams.BackBufferFormat = XnaSurfaceFormat.Color;
            PlatformInitialiseGraphicsDevice(ref presParams);

            // Try to create graphics device with hi-def profile
            try 
            {
                graphicsDevice = new GraphicsDevice(GraphicsAdapter.DefaultAdapter, GraphicsProfile.HiDef, presParams);
            }
            // Otherwise, if unsupported defer to using the low-def profile
            catch (NotSupportedException)
            {
                graphicsDevice = new GraphicsDevice(GraphicsAdapter.DefaultAdapter, GraphicsProfile.Reach, presParams);
            }

            DrawManager = new CCDrawManager(graphicsDevice);

            CCDrawManager.SharedDrawManager = DrawManager;

            graphicsDeviceService = new CCGraphicsDeviceService(graphicsDevice);
        }

        void InitialiseResourceCaches()
        {
            ContentManager = new CCContentManager(new GameServiceContainer());
            FontAtlasCache = new CCFontAtlasCache();
            SpriteFontCache = new CCSpriteFontCache(ContentManager);
            SpriteFrameCache = new CCSpriteFrameCache();
            ParticleSystemCache = new CCParticleSystemCache(Scheduler);
            TextureCache = new CCTextureCache(Scheduler);
            AnimationCache = new CCAnimationCache();

            // Link new caches to singleton instances
            // For now, this is required because a layer's resources (e.g. sprite)
            // may be loaded prior to a gameview being associated with that layer
            CCContentManager.SharedContentManager = ContentManager;
            CCFontAtlasCache.SharedFontAtlasCache = FontAtlasCache;
            CCSpriteFontCache.SharedSpriteFontCache = SpriteFontCache;
            CCSpriteFrameCache.SharedSpriteFrameCache = SpriteFrameCache;
            CCParticleSystemCache.SharedParticleSystemCache = ParticleSystemCache;
            CCTextureCache.SharedTextureCache = TextureCache;
            CCAnimationCache.SharedAnimationCache = AnimationCache;

            var serviceProvider = ContentManager.ServiceProvider as GameServiceContainer;
            serviceProvider.AddService(typeof(IGraphicsDeviceService), graphicsDeviceService);
        }

        void InitialiseRunLoop()
        {
            gameTimer = Stopwatch.StartNew();
            gameTime = new GameTime();

            accumulatedElapsedTime = TimeSpan.Zero;
            targetElapsedTime = TimeSpan.FromTicks(numOfTicksPerUpdate); 
            maxElapsedTime = TimeSpan.FromMilliseconds(maxUpdateTimeMilliseconds);
            previousTicks = 0;
        }

        void LoadGame()
        {
            if (viewInitialised && platformInitialised && !gameLoaded && viewCreated != null)
            {
                viewCreated(this, null);
                gameLoaded = true;
            }
        }

        #endregion Initialisation


        #region Cleaning up

        ~CCGameView() 
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected override void Dispose(bool disposing)
        {
            // Here we check this == null just in case we are coming from a visual designer
            // like Visual Studio XAML designer.  This may need to be revisited later
            // after other platforms and devices have been implemented.
            if(disposed || this == null)
                return;

            PlatformDispose(disposing);

            // We can't guarantee when Dispose will be called, so
            // check if we can safely attempt to dispose of graphics device
            bool canDisposeGraphics = PlatformCanDisposeGraphicsDevice();

            if (disposing) 
            {
                if (AudioEngine != null)
                {
                    AudioEngine.Dispose();
                    AudioEngine = null;
                }

                if (graphicsDevice != null && canDisposeGraphics)
                {
                    graphicsDevice.Dispose();
                    graphicsDevice = null;
                }
            }

            var serviceProvider = CCContentManager.SharedContentManager.ServiceProvider as GameServiceContainer;
            serviceProvider.RemoveService(typeof(IGraphicsDeviceService));

            currentViewInstance = null;

            disposed = true;

            // If the graphics context is lost, then it's probably not safe to call
            // the native view's base Dispose method which would rely on an active context
            if (canDisposeGraphics)
                base.Dispose(disposing);
        }

        // MonoGame maintains static references to the underlying context, and more generally, 
        // there's no guarantee that a previous instance of CCGameView will have been disposed by the GC.
        // However, given the limitation imposed by MonoGame to only have one active instance, we need
        // to ensure that this indeed the case and explicitly Dispose of any old game views.
        void RemoveExistingView()
        {
            if (currentViewInstance != null && currentViewInstance.Target != this) 
            {
                var prevGameView = currentViewInstance.Target as CCGameView;

                if (prevGameView != null) 
                {
                    prevGameView.Paused = true;
                    prevGameView.Dispose();
                }
            }
        }

        #endregion Cleaning up


        #region Drawing

        internal void Present()
        {
            PlatformPresent();
        }

        void UpdateViewport()
        {
            int width = ViewSize.Width;
            int height = ViewSize.Height;

            // The GraphicsDevice BackBuffer dimensions are used by MonoGame when laying out the viewport
            // so make sure they're updated
            graphicsDevice.PresentationParameters.BackBufferWidth = width;
            graphicsDevice.PresentationParameters.BackBufferHeight = height;

            if (resolutionPolicy != CCViewResolutionPolicy.Custom)
            {
                float resolutionScaleX = width / (float)DesignResolution.Width;
                float resolutionScaleY = height / (float)DesignResolution.Height;

                switch (resolutionPolicy)
                {
                    case CCViewResolutionPolicy.NoBorder:
                        resolutionScaleX = resolutionScaleY = Math.Max(resolutionScaleX, resolutionScaleY);
                        break;
                    case CCViewResolutionPolicy.ShowAll:
                        resolutionScaleX = resolutionScaleY = Math.Min(resolutionScaleX, resolutionScaleY);
                        break;
                    case CCViewResolutionPolicy.FixedHeight:
                        resolutionScaleX = resolutionScaleY;
                        designResolution.Width = (int)Math.Ceiling(width / resolutionScaleX);
                        break;
                    case CCViewResolutionPolicy.FixedWidth:
                        resolutionScaleY = resolutionScaleX;
                        designResolution.Height = (int)Math.Ceiling(height / resolutionScaleY);
                        break;
                    default:
                        break;
                }

                float viewPortW = DesignResolution.Width * resolutionScaleX;
                float viewPortH = DesignResolution.Height * resolutionScaleY;

                CCRect viewPortRect = new CCRect((width - viewPortW) / 2, (height - viewPortH) / 2, 
                    viewPortW, viewPortH);

                viewportRatio = new CCRect(
                    ((viewPortRect.Origin.X) / width),
                    ((viewPortRect.Origin.Y) / height),
                    ((viewPortRect.Size.Width) / width),
                    ((viewPortRect.Size.Height) / height)
                );
            }

            Viewport = new Viewport((int)(width * viewportRatio.Origin.X), (int)(height * viewportRatio.Origin.Y), 
                (int)(width * viewportRatio.Size.Width), (int)(height * viewportRatio.Size.Height));

            CCPoint center = new CCPoint(ViewSize.Width / 2.0f, ViewSize.Height / 2.0f);
            defaultViewMatrix = XnaMatrix.CreateLookAt(new CCPoint3(center, 300.0f).XnaVector, new CCPoint3(center, 0.0f).XnaVector, Vector3.Up);
            defaultProjMatrix = XnaMatrix.CreateOrthographic(ViewSize.Width, ViewSize.Height, 1024f, -1024);
            defaultViewport = new Viewport(0, 0, ViewSize.Width, ViewSize.Height);

            viewportDirty = false;
        }

        void Draw()
        {
            DrawManager.BeginDraw();

            CCScene runningScene = Director.RunningScene;

            var vp = Viewport;

            if (runningScene != null) 
            {
                try {
                    Renderer.PushViewportGroup(ref vp);

                    runningScene.Visit();

                    Renderer.PopViewportGroup();

                    Renderer.VisitRenderQueue();
                }
                catch(Exception e)
                {
                    LogMessage ("CCGameView: " + e.Message);
                }
            }

            if (Stats.Enabled)
            {
                try {
                    Renderer.PushGroup();
                    Renderer.PushViewportGroup(ref defaultViewport);
                    Renderer.PushLayerGroup(ref defaultViewMatrix, ref defaultProjMatrix);

                    DrawManager.UpdateStats();
                    Stats.Draw(this);

                    Renderer.PopLayerGroup();
                    Renderer.PopViewportGroup();
                    Renderer.PopGroup();

                    Renderer.VisitRenderQueue();
                }
                catch (Exception e) {
                    string msg = new System.Text.StringBuilder ()
                    .AppendLine ($"Draw.Stats.Enabled : {e.Message}")
                    .AppendLine ($"Stack Trace: {e.StackTrace}")
                    .ToString ();

                    LogMessage (msg);
                }
            } 

            DrawManager.EndDraw();
        }

        #endregion Drawing


        #region Run loop

        void Tick()
        {
            RetryTick:

            var currentTicks = gameTimer.Elapsed.Ticks;
            accumulatedElapsedTime += TimeSpan.FromTicks(currentTicks - previousTicks);
            previousTicks = currentTicks;

            if (accumulatedElapsedTime < targetElapsedTime)
            {
                var sleepTime = (int)(targetElapsedTime - accumulatedElapsedTime).TotalMilliseconds;

                Task.Delay(sleepTime).Wait();

                goto RetryTick;
            }

            if (accumulatedElapsedTime > maxElapsedTime)
                accumulatedElapsedTime = maxElapsedTime;

            gameTime.ElapsedGameTime = targetElapsedTime;
            var stepCount = 0;

            while (accumulatedElapsedTime >= targetElapsedTime)
            {
                gameTime.TotalGameTime += targetElapsedTime;
                accumulatedElapsedTime -= targetElapsedTime;
                ++stepCount;

                Update(gameTime);
            }

            gameTime.ElapsedGameTime = TimeSpan.FromTicks(targetElapsedTime.Ticks * stepCount);
        }

        void Update(GameTime time)
        {
            try {
                float deltaTime = (float)gameTime.ElapsedGameTime.TotalSeconds;

                Stats.UpdateStart();

                SoundEffectInstancePool.Update();

                if (Director.NextScene != null)
                    Director.SetNextScene();

                CCScheduler.SharedScheduler.Update(deltaTime);
                ActionManager?.Update(deltaTime);

                ProcessInput();

                Stats.UpdateEnd(deltaTime);
            }
            catch (Exception e) {
                string msg = new System.Text.StringBuilder ()
                    .AppendLine ($"Draw.Stats.Enabled : {e.Message}")
                    .AppendLine ($"Stack Trace: {e.StackTrace}")
                    .ToString ();

                LogMessage (msg);
            }
        }

        #endregion Run loop

        protected virtual void LogMessage(string msg)
        {
            System.Diagnostics.Debug.WriteLine ("CCGameView: " + msg);
        }
    }
}

