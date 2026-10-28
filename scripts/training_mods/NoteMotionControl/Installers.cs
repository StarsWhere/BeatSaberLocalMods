using SiraUtil.Submissions;
using Zenject;

namespace NoteMotionControl
{
    /// <summary>
    /// Keeps the scene container available without resolving any scene roots
    /// while installers are still running. The native speed Init prefix calls
    /// EnsurePrepared before the game consumes the song's speed parameters.
    /// </summary>
    internal static class EarlySession
    {
        private static readonly object Gate = new object();
        private static DiContainer container;

        internal static void Register(DiContainer value)
        {
            if (value == null) return;
            lock (Gate) container = value;
        }

        internal static bool EnsurePrepared()
        {
            DiContainer current;
            lock (Gate) current = container;
            if (current == null) return false;

            RuntimeSession session = null;
            try
            {
                session = current.TryResolve<RuntimeSession>();
                if (session == null)
                {
                    RuntimeState.ErrorOnce(
                        "early-session-resolve",
                        "Note Training Control could not resolve its gameplay session before movement timing was consumed.");
                    return false;
                }

                return session.Prepare();
            }
            catch (System.Exception error)
            {
                if (session != null && session.HasReducedChart) throw;
                RuntimeState.ErrorOnce(
                    "early-session-prepare",
                    "Note Training Control could not prepare its gameplay session before movement timing was consumed.",
                    error);
                return false;
            }
        }

        internal static void Release(DiContainer value)
        {
            if (value == null) return;
            lock (Gate)
            {
                if (ReferenceEquals(container, value)) container = null;
            }
        }

        internal static void Clear()
        {
            lock (Gate) container = null;
        }
    }

    internal sealed class PlayerInstaller : Installer
    {
        public override void InstallBindings()
        {
            EarlySession.Register(Container);
            Container.BindInterfacesAndSelfTo<RuntimeSession>().AsSingle().NonLazy();
        }
    }

    internal sealed class MenuInstaller : Installer
    {
        public override void InstallBindings()
        {
            Container.BindInterfacesTo<SettingsMenu>().AsSingle();
        }
    }

    internal sealed class RuntimeSession : IInitializable, System.IDisposable
    {
        private GameplayCoreSceneSetupData setup;
        private Submission submission;
        private DiContainer container;
        private bool preparing;
        private bool prepared;

        internal bool HasReducedChart => TrainingLoad.ReductionApplied(setup);

        [Inject]
        private void Construct(
            GameplayCoreSceneSetupData sceneSetup,
            [InjectOptional] Submission scoreSubmission,
            DiContainer sceneContainer)
        {
            setup = sceneSetup;
            submission = scoreSubmission;
            container = sceneContainer;
        }

        public void Initialize()
        {
            Prepare();
        }

        internal bool Prepare()
        {
            if (prepared) return true;
            if (preparing) return false;
            if (setup == null || container == null)
            {
                RuntimeState.ErrorOnce(
                    "early-session-input",
                    "Note Training Control could not prepare gameplay because its scene setup or container is missing.");
                return false;
            }

            preparing = true;
            try
            {
                if (!RuntimeState.Begin(setup, submission, this, container)) return false;
                prepared = true;
                return true;
            }
            catch (System.Exception error)
            {
                RuntimeState.ErrorOnce(
                    "early-session-begin",
                    "Note Training Control could not begin gameplay preparation before movement timing was consumed.",
                    error);
                RuntimeState.End(this, setup, container);
                if (HasReducedChart) throw;
                return false;
            }
            finally
            {
                preparing = false;
            }
        }

        public void Dispose()
        {
            RuntimeState.End(this, setup, container);
            TrainingLoad.End(setup);
            EarlySession.Release(container);
            prepared = false;
        }
    }
}
