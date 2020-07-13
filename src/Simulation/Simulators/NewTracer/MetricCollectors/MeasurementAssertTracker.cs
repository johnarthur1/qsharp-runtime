﻿using Microsoft.Quantum.Simulation.Common;
using Microsoft.Quantum.Simulation.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Quantum.Simulation.Simulators.NewTracer.MetricCollection;

namespace Microsoft.Quantum.Simulation.Simulators.NewTracer.MetricCollectors
{
    //constraint logic copied over from qctracesimulator for now

    //TODO: better name
    /// <summary>
    /// Provides limited support for measurements when using the tracer as a standalone without a proper
    /// qubit simulator.
    /// </summary>
    public class MeasurementAssertTracker : QuantumProcessorBase, IMetricCollector, IQuantumProcessor, IQubitTraceSubscriber
    {
        public class UnconstrainedMeasurementCount : IStackRecord
        {
            public double UnconstrainedMeasurements { get; set; }
        }

        protected UnconstrainedMeasurementCount CurrentState;
        protected Random RandomGenerator;
        protected bool ThrowOnUnconstrainedMeasurement;

        public MeasurementAssertTracker(bool throwOnUnconstrainedMeasurement)
        {
            this.RandomGenerator = new Random(); //TODO: how should this be configured?
            this.ThrowOnUnconstrainedMeasurement = throwOnUnconstrainedMeasurement;
            this.CurrentState = new UnconstrainedMeasurementCount();
        }

        public string CollectorName()
        {
            return "MeasurementAssertTracker";
        }

        public IList<string> Metrics()
        {
            return new string[]
            {
                "UnconstrainedMeasurements"
            };
        }

        public double[] OutputMetricsOnOperationEnd(IStackRecord startState, IApplyData returned)
        {
            UnconstrainedMeasurementCount prevState = (UnconstrainedMeasurementCount)startState;
            return new double[]
            {
                    CurrentState.UnconstrainedMeasurements - prevState.UnconstrainedMeasurements
            };
        }

        public IStackRecord SaveRecordOnOperationStart(IApplyData _)
        {
            return new UnconstrainedMeasurementCount
            {
                UnconstrainedMeasurements = CurrentState.UnconstrainedMeasurements
            };
        }

        //TODO: discuss with Vadym whether or not there will be issues with entanglement

        public Result UnconstrainedMeasurementResult()
        {
            if (this.ThrowOnUnconstrainedMeasurement)
            { throw new UnconstrainedMeasurementException(); }

            this.CurrentState.UnconstrainedMeasurements++;
            return Result.Zero;
        }

        private IEnumerable<QubitConstraint> ExtractConstraints(IEnumerable<Qubit> qubits)
        {
            return qubits.Select
                (qubit => (qubit as TraceableQubit).ExtractData(this) as QubitConstraint);
        }

        public void Assert(IQArray<Pauli> bases, IQArray<Qubit> qubits, Result result, string msg)
        {
            double probabilityOfZero = result == Result.Zero ? 1.0 : 0.0;
            double tol = 0.00001; //TODO: what should this be?
            this.AssertProb(bases, qubits, probabilityOfZero, msg, tol);
        }

        public void AssertProb(IQArray<Pauli> bases, IQArray<Qubit> qubits, double probabilityOfZero, string msg, double tol)
        {
            MeasurementConstraint mConstraint = MeasurementConstraint.AssertMeasurement(bases, Result.Zero, probabilityOfZero);
            QubitConstraint[] constraints = this.ExtractConstraints(qubits).ToArray();
            QubitConstraint.SetConstraint(constraints, mConstraint);
        }

        public void InvalidateConstraint(Qubit qubit)
        {
            TraceableQubit q = qubit as TraceableQubit;
            QubitConstraint c = q.ExtractData(this) as QubitConstraint;
            c.OnUseQubit();
        }

        private void InvalidateConstraints(IQArray<Qubit> qubits)
        {
            foreach (QubitConstraint c in this.ExtractConstraints(qubits))
            {
                c.OnUseQubit();
            }
        }

        public override Result M(Qubit qubit)
        {
            return this.Measure(new QArray<Pauli>(Pauli.PauliZ), new QArray<Qubit>(qubit));
        }

        public override Result Measure(IQArray<Pauli> bases, IQArray<Qubit> qubits)
        {

            if (bases.Count == 0)
            {
                return Result.Zero;
            }

            QubitConstraint[] constraints = this.ExtractConstraints(qubits).ToArray();
            QubitConstraint qubit0Constraint = constraints[0];

            if (qubit0Constraint.Constraint == null)
            {
                return UnconstrainedMeasurementResult();
            }
            else
            {
                for (int i = 0; i < qubits.Count; ++i)
                {
                    QubitConstraint qubitIConstraint = constraints[i];

                    // makes sure that none of the qubits constraints have been invalidated
                    if (qubitIConstraint.Constraint == null)
                    {
                        return UnconstrainedMeasurementResult();
                    }

                    // makes sure that all qubits involved have the same constraint
                    if (qubitIConstraint.Constraint != qubit0Constraint.Constraint)
                    {
                        return UnconstrainedMeasurementResult();
                    }

                    // makes sure that constrain's observable matches observable being measured
                    if (qubitIConstraint.QubitPauli != bases[i])
                    {
                        return UnconstrainedMeasurementResult();
                    }
                }
            }

            // here we have ensured that all conditions we need to predict the measurement outcome are met
            MeasurementConstraint constraint = qubit0Constraint.Constraint;
            if (constraint.Type == MeasurementConstraint.ConstraintType.Force)
            {
                return constraint.ConstrainToResult;
            }
            else if (constraint.Type == MeasurementConstraint.ConstraintType.Assert)
            {
                Double sample = this.RandomGenerator.NextDouble();
                if (sample <= constraint.Probability)
                {
                    Debug.WriteLine($"Measurement outcome with probability {constraint.Probability} happened, result is {constraint.ConstrainToResult}");
                    return constraint.ConstrainToResult;
                }
                else
                {
                    Result opposite = constraint.ConstrainToResult == Result.Zero ? Result.One : Result.Zero;
                    Debug.WriteLine($"Measurement outcome with probability {constraint.Probability} did not happen, result is {opposite}");
                    return opposite;
                }
            }
            Debug.Assert(false, "This point should not be reached.");
            return UnconstrainedMeasurementResult();
        }

        public object NewTracingData(long id)
        {
            return QubitConstraint.ZeroStateConstraint();
        }


        //
        // Primite operations that the GateCounter expects other operations to be decomposed to.
        //

        public override void Z(Qubit qubit)
        {
            this.InvalidateConstraint(qubit);
        }

        public override void ControlledZ(IQArray<Qubit> controls, Qubit qubit)
        {
            if (controls.Length == 1 || controls.Length == 2)
            {
                this.InvalidateConstraints(controls);
                this.InvalidateConstraint(qubit);
            }
            else
            {
                throw new NotImplementedException();
            }
        }

        public override void H(Qubit qubit)
        {
            this.InvalidateConstraint(qubit);
        }

        public override void S(Qubit qubit)
        {
            this.InvalidateConstraint(qubit);
        }

        public override void SAdjoint(Qubit qubit)
        {
            this.InvalidateConstraint(qubit);
        }
        public override void T(Qubit qubit)
        {
            this.InvalidateConstraint(qubit);
        }

        public override void TAdjoint(Qubit qubit)
        {
            this.InvalidateConstraint(qubit);
        }

        public override void SWAP(Qubit qubit1, Qubit qubit2)
        {
            //no-op
        }

        public override void R(Pauli axis, double theta, Qubit qubit)
        {
            if (axis == Pauli.PauliZ)
            {
                this.InvalidateConstraint(qubit);
            }
            else
            {
                throw new NotImplementedException();
            }
        }
    }

    /// <summary>
    /// Holds data describing measurement constraint associated to a 
    /// given qubit at a given execution moment. 
    /// </summary>
    public class QubitConstraint
    {
        public QubitConstraint(MeasurementConstraint constraint, uint qubitPosition)
        {
            Debug.Assert(constraint != null);
            this.Set(constraint, qubitPosition);
        }

        public void Set(MeasurementConstraint constraint, uint qubitPosition)
        {
            Debug.Assert(constraint != null);
            Debug.Assert(qubitPosition < constraint.Observable.Length);
            Constraint = constraint;
            QubitPositionInConstraint = qubitPosition;
        }

        public static QubitConstraint ZeroStateConstraint()
        {
            return new QubitConstraint(MeasurementConstraint.ZeroStateAssert(), 0);
        }

        /// <summary>
        /// If qubit was used, in other words some unitary or measurement were applied to it, 
        /// the constraint gets invalidated.
        /// </summary>
        public void OnUseQubit()
        {
            Constraint = null;
        }

        /// <summary>
        /// Released qubits assumed to be in a zero state
        /// </summary>
        public void OnRelease()
        {
            Constraint = MeasurementConstraint.ZeroStateAssert();
            QubitPositionInConstraint = 0;
        }

        /// <summary>
        /// Pauli in the constraint assigned to this qubit
        /// </summary>
        /// <returns></returns>
        public Pauli QubitPauli
        {
            get
            {
                return Constraint.Observable[(int)QubitPositionInConstraint];
            }
        }

        public static void SetConstraint(QubitConstraint[] qubitConstraints, MeasurementConstraint constraint)
        {
            Debug.Assert(qubitConstraints != null);
            Debug.Assert(constraint != null);

            for (uint i = 0; i < qubitConstraints.Length; ++i)
            {
                qubitConstraints[i].Set(constraint, i);
            }
        }

        public MeasurementConstraint Constraint { get; private set; }
        public uint QubitPositionInConstraint { get; private set; }
    }

    public class MeasurementConstraint
    {
        public enum ConstraintType
        {
            /// <summary>
            /// Indicates that constraint corresponds to the assertion that given
            /// measurement outcome should happen with given probability. Assertion
            /// is the fact that the user knows about the computation they are writing.
            /// </summary>
            Assert,
            /// <summary>
            /// Indicates that constraint corresponds to the user wanting to enforce
            /// the measurement outcome for debugging purposes. User does not know that
            /// this particular outcome will happen, but they want to observe execution path
            /// when this outcome happens.
            /// </summary>
            Force
        }

        public ConstraintType Type { get; private set; }

        /// <summary>
        /// The observable being measured
        /// </summary>
        public Pauli[] Observable { get; private set; }

        /// <summary>
        /// The measurement outcome that should be forced or
        /// should happen with the given probability
        /// </summary>
        public Result ConstrainToResult { get; private set; }

        /// <summary>
        /// Probability with which asserted outcome should happen.
        /// </summary>
        public double Probability { get; private set; }

        /// <summary>
        /// Returns measurement constraint object corresponding to
        /// a given qubit being in zero state
        /// </summary>
        public static MeasurementConstraint ZeroStateAssert()
        {
            MeasurementConstraint m = new MeasurementConstraint
            {
                Type = ConstraintType.Assert,
                Observable = new Pauli[] { Pauli.PauliZ },
                ConstrainToResult = Result.Zero,
                Probability = 1.0,
            };
            return m;
        }

        /// <summary>
        /// Returns measurement constraint corresponding to the user enforcing given outcome.
        /// </summary>
        public static MeasurementConstraint ForceMeasurement(IEnumerable<Pauli> observable, Result result)
        {
            Debug.Assert(observable != null);

            MeasurementConstraint m = new MeasurementConstraint
            {
                Type = ConstraintType.Force,
                Observable = observable.ToArray(),
                ConstrainToResult = result,
            };
            return m;
        }

        /// <summary>
        /// Returns measurement constraint corresponding to user asserting that given measurement should
        /// happen with given probability.
        /// </summary>
        public static MeasurementConstraint AssertMeasurement(IEnumerable<Pauli> observable, Result result, double probability)
        {
            Debug.Assert(observable != null);

            MeasurementConstraint m = new MeasurementConstraint
            {
                Type = ConstraintType.Assert,
                Observable = observable.ToArray(),
                ConstrainToResult = result,
                Probability = probability
            };
            return m;
        }
    }
}
