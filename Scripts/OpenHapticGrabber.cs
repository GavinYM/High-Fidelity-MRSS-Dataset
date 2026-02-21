﻿using System;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ynnu.OpenHaptic
{
	public class OpenHapticGrabber : HapticManipulator
	{
		[Header("Grabber Attributes")]

		[Header("Export (Grab F_d / F_time)")]
		public bool m_enableExport = true;
		[Tooltip("Optional recorder reference. If null, will auto-find in scene (HapticSimRunRecorder.Instance or FindObjectOfType).")]
		public HapticSimRunRecorder m_recorder = null;

		[Tooltip("If true, d = |stylus - particle| (recommended). If particle not available, fallback to stylus displacement from grab start.")]
		public bool m_useStylusToParticleDistance = true;

		[Range(0.001f, 1.100f)] public float m_grabSensity = 0.015f;         
		[Range(0.01f, 1.00f)] public float m_grabForceCoef = 0.05f;         
		[Range(0.0f, 180.0f)] public float m_grabForceSmoothAngle = 90.0f;   

		[Header("Biomech Curve Force (Fast Route)")]
		public bool m_useCurveForce = true;      
		public BiomechForceCurve m_curve;        
		public bool m_xIsStrain = true;          
		public float m_thicknessFallback = 0.03f; 

		[Header("Visco (optional)")]
		public bool m_enableVisco = false;
		public float m_eta = 0.0f;              
		public float m_tau = 0.0f;               

		[Header("Prony Series (optional, generalized Maxwell)")]
		[Tooltip("Enable Prony-series viscoelastic overstress (history-dependent). Recommended to use this instead of simple eta*|xDot| when you have fitted Prony parameters.")]
		public bool m_enableProny = false;

		[Tooltip("Prony coefficients K_i (units: N if x is strain; N/m if x is displacement). Must have same length as m_pronyTau.")]
		public List<float> m_pronyK = new List<float>() { 0.0f };
		[Tooltip("Relaxation time constants tau_i (seconds). Must have same length as m_pronyK.")]
		public List<float> m_pronyTau = new List<float>() { 0.1f };

		private float[] _pronyQi = null;
		private bool _pronyInit = false;

		private Vector3 m_grabAnchorWorld = Vector3.zero;
		private float m_effThickness = 0.03f;

		private float m_lastX = 0f;
		private float m_forceLPF = 0f;

		private float m_lastD = 0f;
		private float m_lastD_export = 0f;

		// Export state
		private Vector3 m_grabStartStylusWorld = Vector3.zero;
		private int m_grabParticleIndex = -1;

		private OpenHapticPluginGrabber mHaptic = null;
		private OpenHapticPluginGrabber mSphere = null;  
		private float mGrabFoceSmoothCos = 0f;           

		private GameObject mNipper1 = null;
		private GameObject mNipper2 = null;

		int[] m_indexOfPartical = new int[10];
		int m_countOfPartical = 0;
		float[] dis = new float[10];
		Vector3 currentPosition = Vector3.zero;

		private Vector3 mLastForceDirection = Vector3.zero;
		private Vector3 mCurrentForceDirection = Vector3.zero;

		private MethodInfo _miRecordGrabberSampleEx = null;

		#region message

		void Start()
		{
			mHaptic = null;
			mSphere = null;

			RecomputeSmoothCos();

			// recorder (optional)
			if (m_recorder == null)
				m_recorder = (HapticSimRunRecorder.Instance != null) ? HapticSimRunRecorder.Instance : FindObjectOfType<HapticSimRunRecorder>();

			CacheRecorderMethods();

			var HPs = (OpenHapticPluginGrabber[])UnityEngine.Object.FindObjectsOfType(typeof(OpenHapticPluginGrabber));
			foreach (OpenHapticPluginGrabber HP in HPs)
			{
				if (HP.m_hapticManipulator == this.gameObject)
				{
					mHaptic = HP;
					mSphere = HP;
					break;
				}
			}

			if (!mHaptic) Debug.LogError("HapticGrabber must be attached to the same object as the HapticPluginGrabber script.");

			for (int i = 0; i < gameObject.transform.childCount; ++i)
			{
				var child = gameObject.transform.GetChild(i);
				if (child)
				{
					if (child.gameObject.name == "nipper1")
						mNipper1 = child.gameObject;
					else if (child.gameObject.name == "nipper2")
						mNipper2 = child.gameObject;
				}
			}
			if (!mNipper1) Debug.Log("Can not find nipper1 in OpenHapticGrabber");
			if (!mNipper2) Debug.Log("Can not find nipper2 in OpenHapticGrabber");
			if (mNipper2) mNipper2.SetActive(true);

			InitializeHapticManipulator();  
		}

		private void OnValidate()
		{
			RecomputeSmoothCos();
		}

		private void RecomputeSmoothCos()
		{
			float ang = Mathf.Clamp(m_grabForceSmoothAngle, 0f, 180f);
			mGrabFoceSmoothCos = (float)System.Math.Cos(ang * System.Math.PI / 180.0);
		}

		private void EnsurePronyState()
		{
			int n = Mathf.Min(m_pronyK != null ? m_pronyK.Count : 0, m_pronyTau != null ? m_pronyTau.Count : 0);
			if (n <= 0)
			{
				_pronyQi = null;
				_pronyInit = false;
				return;
			}

			if (_pronyQi == null || _pronyQi.Length != n)
			{
				_pronyQi = new float[n];
				_pronyInit = false;
			}
		}

		private void ResetPronyState(float x0 = 0f)
		{
			EnsurePronyState();
			if (_pronyQi == null) return;
			for (int i = 0; i < _pronyQi.Length; i++)
				_pronyQi[i] = x0;
			_pronyInit = true;
		}

		private float UpdatePronyOverstress(float x, float dt)
		{
			EnsurePronyState();
			if (_pronyQi == null) return 0f;

			// First frame after enabling / resizing arrays: initialize to current x to avoid an impulse.
			if (!_pronyInit)
				ResetPronyState(x);

			float sum = 0f;
			for (int i = 0; i < _pronyQi.Length; i++)
			{
				float tau = Mathf.Max(m_pronyTau[i], 1e-4f);
				float a = Mathf.Exp(-dt / tau);
				_pronyQi[i] = a * _pronyQi[i] + (1f - a) * x;
				sum += m_pronyK[i] * (x - _pronyQi[i]);
			}
			return sum;
		}

		private void CacheRecorderMethods()
		{
			_miRecordGrabberSampleEx = null;
			if (m_recorder == null) return;

			try
			{
				var t = m_recorder.GetType();
				var ms = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
				for (int i = 0; i < ms.Length; i++)
				{
					if (ms[i].Name == "RecordGrabberSampleEx")
					{
						_miRecordGrabberSampleEx = ms[i];
						break;
					}
				}
			}
			catch { _miRecordGrabberSampleEx = null; }
		}

		void FixedUpdate()
		{
			if (mHaptic)
			{
				bool newButtonStatus = mHaptic.mButtons[m_buttonID] == 1;
				bool oldButtonStatus = mButtonStatus;
				mButtonStatus = newButtonStatus;

				ynnu.Flex.Cutting.Cutting.ParticlePosition(1126, ref currentPosition);
				GameObject.Find("FirstTarget").transform.position = currentPosition;
				ynnu.Flex.Cutting.Cutting.ParticlePosition(1035, ref currentPosition);
				GameObject.Find("SecondTarget").transform.position = currentPosition;
				ynnu.Flex.Cutting.Cutting.ParticlePosition(1404, ref currentPosition);
				GameObject.Find("ThirdTarget").transform.position = currentPosition;

				if (oldButtonStatus == false && newButtonStatus == true)
					ButtonDown();
				if (oldButtonStatus == true && newButtonStatus == false)
					ButtonUp();

				for (int i = 0; i < m_countOfPartical; ++i)
				{
					if (m_indexOfPartical[i] >= 0)
					{
						ynnu.Flex.Cutting.Cutting.ParticlePosition(m_indexOfPartical[i], ref currentPosition);
						GameObject.Find("Sphere" + i).transform.position = currentPosition;
						Debug.Log("第" + i + "个夹子赋予第" + m_indexOfPartical[i] + "个粒子之上，空间位置：" + currentPosition);

						if (i == 0)
						{
							ynnu.Flex.Cutting.Cutting.ParticlePosition(1126, ref currentPosition);
							Vector3 firstPosition = currentPosition;
							dis[i] = (firstPosition - GameObject.Find("Sphere" + i).transform.position).sqrMagnitude;
							Debug.Log("第" + i + "个夹子和目标点的距离：" + dis[i]);
						}
						else if (i == 1)
						{
							ynnu.Flex.Cutting.Cutting.ParticlePosition(1035, ref currentPosition);
							Vector3 secondPosition = currentPosition;
							dis[i] = (secondPosition - GameObject.Find("Sphere" + i).transform.position).sqrMagnitude;
							Debug.Log("第" + i + "个夹子和目标点的距离：" + dis[i]);
						}
					}
				}
			}
		}

		private void Update()
		{
			if (mHaptic == null || !mHaptic.mIsGrabbing)
				return;

			Vector3 stylusW = mHaptic.mStylusPositionWorld;

			Vector3 movePos = stylusW;
			ynnu.Flex.Cutting.Cutting.ParticleMove(ref movePos);

			Vector3 dW = stylusW - m_grabAnchorWorld;
			float dist = dW.magnitude;

			float thickness = Mathf.Max(m_effThickness, 1e-4f);
			float x = m_xIsStrain ? (dist / thickness) : dist;

			float dt = Mathf.Max(Time.deltaTime, 1e-6f);

			if (!m_enableProny)
				_pronyInit = false;

			float xDot = (x - m_lastX) / dt;
			m_lastX = x;

			float dDot_model = (dist - m_lastD) / dt;
			m_lastD = dist;

			float F = 0f;

			if (m_useCurveForce && m_curve != null)
			{
				F = m_curve.Evaluate(Mathf.Abs(x));

				// Prony-series overstress 
				if (m_enableProny)
					F += UpdatePronyOverstress(Mathf.Abs(x), dt);

				if (m_enableVisco)
					F += m_eta * Mathf.Abs(xDot);

				if (m_tau > 0f)
				{
					float a = dt / (m_tau + dt);
					m_forceLPF = Mathf.Lerp(m_forceLPF, F, a);
					F = m_forceLPF;
				}

				F = Mathf.Max(0f, F);

				Vector3 dirW = (dist > 1e-6f) ? (dW / dist) : Vector3.zero;
				Vector3 forceW = F * dirW;

				Vector3 forceLocal = mHaptic.transform.InverseTransformDirection(forceW);
				mCurrentForceDirection = (forceLocal.sqrMagnitude > 1e-12f) ? forceLocal.normalized : Vector3.zero;

				if (Vector3.Dot(mLastForceDirection, mCurrentForceDirection) > mGrabFoceSmoothCos)
				{
					double[] force = {
						-forceLocal.x * m_grabForceCoef,
						-forceLocal.y * m_grabForceCoef,
						-forceLocal.z * m_grabForceCoef
					};
					OHToUnityBridge.setForce(mHaptic.configName, force, OHToUnityBridge.dbl_zeros);
				}
				mLastForceDirection = mCurrentForceDirection;

				// export sample (Grabber) 
				if (m_enableExport && m_recorder != null && m_recorder.IsRunning)
				{
					Vector3 stylusWorld = stylusW;

					Vector3 forceCmdWorld = (-forceW) * m_grabForceCoef;

					int pIdx = m_grabParticleIndex;
					bool hasParticle = false;
					Vector3 particleWorld = new Vector3(float.NaN, float.NaN, float.NaN);
					float dWorld = float.NaN;

					if (pIdx >= 0)
					{
						Vector3 pp = Vector3.zero;
						try
						{
							ynnu.Flex.Cutting.Cutting.ParticlePosition(pIdx, ref pp);
							hasParticle = true;
						}
						catch { hasParticle = false; }

						if (hasParticle)
						{
							particleWorld = pp;
							if (m_useStylusToParticleDistance)
								dWorld = Vector3.Distance(stylusWorld, particleWorld);
						}
					}

					if (float.IsNaN(dWorld))
						dWorld = Vector3.Distance(stylusWorld, m_grabStartStylusWorld);

					float dDotWorld = (dWorld - m_lastD_export) / dt;
					m_lastD_export = dWorld;

					string sampleMode = m_enableVisco ? "Visco" : "Elastic";
					if (m_enableProny)
						sampleMode = (m_enableVisco ? "Visco+Prony" : "Prony");

					string curveCsv = (m_curve != null && m_curve.csv != null) ? m_curve.csv.name : "";
					float curveXScale = (m_curve != null) ? m_curve.xScale : 1f;
					float curveYScale = (m_curve != null) ? m_curve.yScale : 1f;

					bool validForce = (forceCmdWorld.sqrMagnitude > 1e-12f);

					RecordGrabberExport(
						mHaptic.configName,
						mButtonStatus,
						mHaptic.mIsGrabbing,
						stylusWorld,
						pIdx,
						particleWorld,
						dWorld,
						dDotWorld,
						x,
						xDot,
						forceCmdWorld,
						validForce,

						sampleMode,
						m_xIsStrain,
						m_effThickness,
						m_grabForceCoef,
						m_enableVisco,
						m_eta,
						m_tau,
						curveCsv,
						curveXScale,
						curveYScale
					);
				}
			}
			else
			{
				//  Flex
				Vector3 fW = stylusW;
				ynnu.Flex.Cutting.Cutting.ParticleForce(ref fW);

				Vector3 fLocal = mHaptic.transform.InverseTransformDirection(fW);
				mCurrentForceDirection = (fLocal.sqrMagnitude > 1e-12f) ? fLocal.normalized : Vector3.zero;

				if (Vector3.Dot(mLastForceDirection, mCurrentForceDirection) > mGrabFoceSmoothCos)
				{
					double[] force = { -fLocal.x * m_grabForceCoef, -fLocal.y * m_grabForceCoef, -fLocal.z * m_grabForceCoef };
					OHToUnityBridge.setForce(mHaptic.configName, force, OHToUnityBridge.dbl_zeros);
				}
				mLastForceDirection = mCurrentForceDirection;

				if (m_enableExport && m_recorder != null && m_recorder.IsRunning)
				{
					Vector3 stylusWorld = stylusW;
					Vector3 forceCmdWorld = (-fW) * m_grabForceCoef;

					float dWorld = Vector3.Distance(stylusWorld, m_grabStartStylusWorld);
					float dDotWorld = (dWorld - m_lastD_export) / dt;
					m_lastD_export = dWorld;

					string sampleMode = "Physics";
					string curveCsv = "";
					float curveXScale = 1f;
					float curveYScale = 1f;

					RecordGrabberExport(
						mHaptic.configName,
						mButtonStatus,
						mHaptic.mIsGrabbing,
						stylusWorld,
						m_grabParticleIndex,
						new Vector3(float.NaN, float.NaN, float.NaN),
						dWorld,
						dDotWorld,
						x,
						xDot,
						forceCmdWorld,
						(forceCmdWorld.sqrMagnitude > 1e-12f),

						sampleMode,
						m_xIsStrain,
						m_effThickness,
						m_grabForceCoef,
						m_enableVisco,
						m_eta,
						m_tau,
						curveCsv,
						curveXScale,
						curveYScale
					);
				}
			}
		}

		private void RecordGrabberExport(
			string deviceName,
			bool buttonStatus,
			bool isGrabbing,
			Vector3 stylusWorld,
			int particleIndex,
			Vector3 particleWorld,
			float displacementWorld,
			float dDotWorld,
			float x,
			float xDot,
			Vector3 forceCmdWorld,
			bool validForce,

			string sampleModeTag,
			bool xIsStrain,
			float thickness,
			float grabForceCoef,
			bool enableVisco,
			float eta,
			float tau,
			string curveCsvName,
			float curveXScale,
			float curveYScale
		)
		{
			if (m_recorder == null) return;

			if (_miRecordGrabberSampleEx != null)
			{
				try
				{
					object[] args = new object[]
					{
						deviceName,
						buttonStatus,
						isGrabbing,
						stylusWorld,
						particleIndex,
						particleWorld,
						displacementWorld,
						dDotWorld,
						x,
						xDot,
						forceCmdWorld,
						validForce,

						sampleModeTag,
						xIsStrain,
						thickness,
						grabForceCoef,
						enableVisco,
						eta,
						tau,
						curveCsvName,
						curveXScale,
						curveYScale
					};

					_miRecordGrabberSampleEx.Invoke(m_recorder, args);
					return;
				}
				catch
				{
				}
			}

			m_recorder.RecordGrabberSample(
				deviceName,
				buttonStatus,
				isGrabbing,
				stylusWorld,
				particleIndex,
				particleWorld,
				displacementWorld,
				forceCmdWorld,
				validForce
			);
		}

		#endregion

		#region button message

		private void ButtonUp()
		{
			if (mHaptic)
			{
				if (mNipper1 && mNipper2)
				{
					mNipper1.SetActive(false);
					mNipper2.SetActive(true);
				}

				if (mHaptic.mIsGrabbingAuxiliary)
				{
					OHToUnityBridge.setForce(mHaptic.configName, OHToUnityBridge.dbl_zeros, OHToUnityBridge.dbl_zeros);

					ynnu.Flex.Cutting.Cutting.ParticleRelease();

					mHaptic.mIsGrabbingAuxiliary = false;
					Debug.Log(mHaptic.configName + ": ReleaseGrabber");

					m_grabParticleIndex = -1;
					m_lastX = 0f;
					m_lastD = 0f;
					m_lastD_export = 0f;
					m_forceLPF = 0f;
					mLastForceDirection = Vector3.zero;
					if (m_enableProny) ResetPronyState(0f);

					ynnu.OpenHaptic.MyOpenHapticsDevices.release(mHaptic.mHHD);
				}
			}
		}

		private void ButtonDown()
		{
			if (mHaptic)
			{
				if (mNipper1 && mNipper2)
				{
					mNipper1.SetActive(true);
					mNipper2.SetActive(false);
				}

				if (!mHaptic.mIsGrabbingAuxiliary)
				{
					Vector3 position = mHaptic.mStylusPositionWorld;
					if (ynnu.Flex.Cutting.Cutting.ParticleCapture(ref position, m_grabSensity) > 0)
					{
						mHaptic.mIsGrabbingAuxiliary = true;

						m_grabStartStylusWorld = mHaptic.mStylusPositionWorld;
						m_grabAnchorWorld = m_grabStartStylusWorld;

						m_effThickness = Mathf.Max(m_thicknessFallback, 1e-4f);

						m_lastX = 0f;
						m_lastD = 0f;
						m_lastD_export = 0f;
						m_forceLPF = 0f;
						mLastForceDirection = Vector3.zero;
						if (m_enableProny) ResetPronyState(0f);

						m_grabParticleIndex = ynnu.Flex.Cutting.Cutting.ParticleCurrent();

						
						if (m_countOfPartical < m_indexOfPartical.Length)
							m_indexOfPartical[m_countOfPartical++] = m_grabParticleIndex;

						OHToUnityBridge.setSpringStiffness(mHaptic.configName, 0.0, 0.0);
						Debug.Log(mHaptic.configName + ": BeginGrabber");

						ynnu.OpenHaptic.MyOpenHapticsDevices.grab(mHaptic.mHHD);
					}
				}
			}
		}

		#endregion
	}
}