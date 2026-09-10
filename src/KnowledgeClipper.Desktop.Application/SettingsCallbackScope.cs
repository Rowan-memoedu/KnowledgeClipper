using System;
using System.Threading;

namespace KnowledgeClipper.Desktop.Application.V3
{
    // Window-local observation ownership, never a Core binding or durable identity.
    internal sealed class SettingsCallbackScope : IDisposable
    {
        private long generation;
        private bool closed;
        private Lease active;
        internal sealed class Lease : IDisposable
        {
            internal readonly long Generation;
            internal readonly string Profile, Selector;
            internal readonly object Binding;
            internal readonly CancellationTokenSource Cancellation = new(TimeSpan.FromSeconds(60));
            internal Lease(long generation, string profile, string selector, object binding)
            { Generation = generation; Profile = profile; Selector = selector; Binding = binding; }
            public void Dispose() => Cancellation.Dispose();
        }
        internal Lease Begin(string profile, string selector, object binding)
        {
            if (closed) throw new ObjectDisposedException(nameof(SettingsCallbackScope));
            Invalidate();
            return active = new Lease(generation, profile, selector, binding);
        }
        internal bool IsCurrent(Lease lease, string profile, string selector, object binding) => !closed &&
            ReferenceEquals(active, lease) && generation == lease.Generation && !lease.Cancellation.IsCancellationRequested &&
            lease.Profile == profile && lease.Selector == selector && ReferenceEquals(lease.Binding, binding);
        internal void Finish(Lease lease) { if (ReferenceEquals(active, lease)) active = null; }
        internal void Invalidate() { generation++; active?.Cancellation.Cancel(); active = null; }
        public void Dispose() { Invalidate(); closed = true; }
    }
}
