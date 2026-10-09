using System;

namespace CargoCore
{
    public enum OperationPhase
    {
        Attaching, Secured, Releasing, Released
    }

    public sealed class CargoOperation
    {
        public Assignment State;
        public int Requester, CargoHandle, TrailerHandle;
        public DateTime Deadline;
        public OperationPhase Phase { get; private set; } = OperationPhase.Attaching;
        public bool Confirmed
        {
            get
            {
                return Phase == OperationPhase.Secured || Phase == OperationPhase.Released;
            }
        }

        public bool ReservesSlot
        {
            get
            {
                return Phase != OperationPhase.Released;
            }
        }

        public bool Confirm(int revision)
        {
            if (State.Revision != revision || Confirmed)
                return false;
            Phase = State.Attached ? OperationPhase.Secured : OperationPhase.Released;
            return true;
        }

        public void Release(int revision, DateTime now)
        {
            if (revision <= State.Revision)
                throw new ArgumentException("Revision must increase");
            State.Attached = false;
            State.Revision = revision;
            Phase = OperationPhase.Releasing;
            Deadline = now.AddSeconds(10);
        }

        public bool AttachTimedOut(DateTime now)
        {
            return Phase == OperationPhase.Attaching && now > Deadline;
        }
    }
}
