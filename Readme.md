# Viscoelastic Lung Modeling for Enhanced Mixed Reality Surgical Training
## 📑 Abstract
In the realm of minimally invasive surgery (MIS), training systems must address the complexity of procedures. Nevertheless, many existing simulators reduce soft-tissue mechanics to Maxwell or Kelvin–Voigt formulations and rely heavily on subjective ratings, which degrades haptic fidelity and constrains objective skill assessment. This paper introduces a position-based dynamics (PBD) viscoelastic lung model, integrating biomechanical data fitting constraints to enhance soft-tissue mechanics in mixed reality (MR) surgical training. Our MR platform incorporates four thoracoscopic modules, logging task-level metrics and kinematic trajectories for objective assessment. To quantify the effect of viscoelastic modeling, we perform 10 paired ablation trials under an identical task protocol. Ablation trials reveal that the viscoelastic model increases feedback force by 9.2% and total mechanical work by 21.1% compared to purely elastic models. Meanwhile, evaluations with 15 physicians demonstrate the system's ability to discriminate skill levels and improve surgical performance. Our findings underscore the system's potential for surgical education and preoperative preparation. The original data are openly available at  https://github.com/GavinYM/High-Fidelity-MRSS-Dataset.

## 🖥️ Dependencies
- Unity 2018.4.0 LTS
- Geomagic Touch 2021 LTS 
 - NVIDIA Flex Unity plugin 

Note: The interfaces files of Flex should be obtained through legal channels. This repository does not distribute restricted components

## 🖼️ Repository Structure
- `data_raw_biomechanics/`: The biomechanical data obtained by cutting and pressing the pig lungs
- `data_raw_training/`:Trainee's original experimental data

- `Scripts/`
    - `OpenHapticGrabber.cs`: Interaction Capture and Viscoelastic Force Calculation 
    - `BiomechForceCurve.cs`: CSV Curve Reading and Interpolation

## 📊 Data
- `data_raw_biomechanics/`:
Raw biomechanical test recordings of porcine lung tissue acquired with a mechanical testing machine.

- `data_raw_training/`:
Raw experimental logs collected from the MR surgical training system. These data support the quantitative analyses reported in the paper.

Privacy: all personal identifiers are removed or anonymized before upload.

## 🎬 Quick Start
1. Attach `BiomechForceCurve` to the scene object, specify `csv`, and set `xScale/yScale`.

2. Attach `OpenHapticGrabber` to the haptic controller object:

- Enable `m_useCurveForce`, Bind `m_curve` to the to the `BiomechForceCurve` component, Set `m_xIsStrain` 


3. Choose one viscoelastic option:

### Option A — Pure Elastic:

- In `OpenHapticGrabber`: `m_useCurveForce = true`, `m_enableProny = false`, `m_enableVisco = false`, `m_tau = 0`

### Option B — Prony Viscoelastic:
- In OpenHapticGrabber: `m_useCurveForce = true`, `m_enableProny = true`
- Check `m_enableProny`, set `m_pronyK` and `m_pronyTau`, keep `m_enableVisco = false`, `m_tau = 0`

4. Run the scene and perform the grasping interaction.

## 📚 Citation

If you use this repository  in your research, please cite our paper:

**Viscoelastic Lung Modeling for Enhanced Mixed Reality Surgical Training**, *The Visual Computer*, <2026>.

### BibTeX
```bibtex
@article{<gao2026visco>,
  title   = {<A Viscoelastic Lung Modeling Framework for Surgical Skills Training in Mixed Reality>},
  author  = {<Yiming Gao>, <Kai Qian>, <Zijun Wang>, <Junfeng Wang>, <Linfei Wang*> and <Yonghang Tai>},
  journal = {Multimedia System},
  year    = {<2026>},
}
