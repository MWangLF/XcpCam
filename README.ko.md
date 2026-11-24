# XcpCam - FFXIV GPose 비주얼 카메라 워크 편집 플러그인

<p>
  <a href="https://github.com/MWangLF/XcpCam/releases"><img alt="Release" src="https://img.shields.io/github/v/release/MWangLF/XcpCam?style=flat-square&label=Release&color=blue"></a>
  <a href="https://github.com/MWangLF/XcpCam/releases"><img alt="Downloads" src="https://img.shields.io/github/downloads/MWangLF/XcpCam/total?style=flat-square&label=Downloads&color=blue"></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/github/license/MWangLF/XcpCam?style=flat-square&label=License&color=blue"></a>
</p>

<p>
  <a href="README.md"><img alt="中文" src="https://img.shields.io/badge/%E4%B8%AD%E6%96%87-lightgrey?style=flat-square"></a>
  <a href="README.en.md"><img alt="English" src="https://img.shields.io/badge/English-lightgrey?style=flat-square"></a>
  <a href="README.ja.md"><img alt="日本語" src="https://img.shields.io/badge/%E6%97%A5%E6%9C%AC%E8%AA%9E-lightgrey?style=flat-square"></a>
  <a href="README.ko.md"><img alt="한국어" src="https://img.shields.io/badge/%ED%95%9C%EA%B5%AD%EC%96%B4-blue?style=flat-square"></a>
</p>

> **현재 버전: 0.1.0.0**. 플러그인은 지속적으로 업데이트되고 있으며, 버그 제보나 기능 제안은 [GitHub Issues](https://github.com/MWangLF/XcpCam/issues)로 남겨 주세요.

**XcpCam**은 FFXIV GPose에서 카메라 워크를 직관적으로 제작·편집할 수 있는 Dalamud 플러그인입니다. 게임 화면에서 직접 카메라 노드, Blender 스타일의 3D 커브, 본(Bone) 추적 제약 조건을 배치하고, 프레임 기반 멀티트랙 타임라인과 그래프 에디터로 정밀하게 다듬을 수 있습니다. XAT / Brio 호환 `.xcp` 파일 내보내기는 물론 **Brio**의 Vivacity 타임라인 및 캐릭터 애니메이션과의 실시간 연동도 지원합니다.

## 주요 특징 및 기능

- **모던 워크스페이스 & 다국어 지원**
  - **Brio Dark + Blender N-Panel** 분할 레이아웃(Dalamud 테마 따르기 전환 가능)을 적용했으며, 경로·제약·Brio 연동·설정을 독립 서브 창으로 열어 타임라인 편집을 방해하지 않습니다.
  - 메인 창을 접은 상태에서도 제어할 수 있는 **하단 고정 미니 타임라인**과 **플로팅 뷰포트 툴바**를 제공하며, **중국어(간체) / English / 日本語 / 한국어** 4개 언어를 기본 내장(기본값은 게임 언어 자동 추종, 설정에서 즉시 전환 가능)합니다.
- **게임 화면 내 직관적인 편집**
  - 카메라 경로(속도에 따른 파랑→빨강 색상 표시), 카메라 프러스텀, 하드 컷 마커, 추적 조준선을 실시간으로 오버레이 표시합니다.
  - 화면에서 카메라 노드, 커브 제어점, 베지어 핸들을 바로 클릭해 선택할 수 있으며, 기즈모가 선택 대상에 자동 전환됩니다. 이동 / 회전 / 크기 조절, 로컬 좌표, 스냅, 다중 선택 그룹 이동 및 회전을 지원합니다.
  - 노드 우클릭 메뉴, GPose 진입/종료 연동 자동 열기·닫기(수동으로 연 창은 유지), 단축키 자유 설정을 제공합니다.
- **멀티트랙 카메라 타임라인 & 하드 컷**
  - 프레임 단위 멀티트랙 타임라인으로 트랙 우선순위 덮어쓰기, 음소거 / 솔로, 드래그 리타이밍, 정지 프레임, 박스 선택, 다단계 실행 취소 / 다시 실행을 지원합니다.
  - **단일 트랙 하드 컷**과 **멀티트랙 전환 컷**을 모두 지원하며, 실시간 미리보기와 Brio 베이크에서 완전한 즉시 전환을 구현합니다.
- **Blender 스타일 3D 커브 및 제약 조건**
  - **커브 편집**: 베지어 커브, 베지어 원, NURBS 경로를 생성하고 게임 내에서 바로 돌출, 세분화, 닫힘 / 방향 전환, 핸들 조정을 할 수 있습니다.
  - **경로 따라가기(Follow Path)**: 3D 커브를 따라 카메라가 이동하며, 타임라인 제어점 표시, 소수점 프레임을 유지하는 타임라인 앵커링, 속도 커브 양방향 동기화를 지원합니다.
  - **본 추적(Bone Tracking)**: Damped Track, Locked Track, Track To 3종 제약을 제공하며, 원하는 캐릭터의 본을 지정해 화면 오프셋, 프레이밍 유지(Keep Framing), 오프라인 내보내기용 궤적 녹화를 사용할 수 있습니다.
- **이중 모드 그래프 에디터**
  - **고급 커브**: 경로 진행률, 위치 오프셋, 연속 회전(다회전 지원), FoV의 베지어 핸들을 직접 편집합니다.
  - **간단 커브**: 공간 경로를 유지한 채 전체 카메라 속도의 완급을 빠르게 조절하며, 위치 / 회전 / FoV 연동 및 개별 조절을 지원합니다.
- **Brio 심층 연동**
  - Brio의 **Vivacity 타임라인** 또는 **캐릭터 애니메이션 스크러버**에 바인딩해 FPS 자동 환산, 총 길이 양방향 동기화, 재생 헤드 연동을 지원합니다.
  - **실시간 카메라 미리보기**, **XAT 연동 재생**, Brio 자유 카메라 트랙 **원클릭 베이크**(실행 취소 지원)를 제공합니다.
- **`.xcp` 가져오기 & 재편집 가능한 프로젝트 내보내기**
  - **편집 가능 프로젝트 내보내기**: `.xcp` 내보내기 시 재생 호환성을 유지하면서 전체 편집 데이터를 함께 저장(기본 활성)하여, 다시 불러오면 트랙·커브·앵커·실시간 본 추적 제약이 그대로 복원됩니다.
  - **일반 `.xcp` 스마트 복원**: 외부 `.xcp` 파일을 편집 가능한 카메라 노드와 커브로 자동 재구성하며, 오차 미리보기와 컷 후보 확인 기능을 제공해 프레임마다 노드가 쌓이지 않습니다.

## 설치 방법

**Dalamud API 15 / .NET 10** 환경이 필요하며, 실시간 미리보기·연동 재생·베이크 기능은 [Brio](https://github.com/Etheirys/Brio)와 함께 사용해야 합니다.

### 📦 Dalamud 사용자 지정 저장소로 설치 (권장)

1. 게임 내에서 `/xlsettings`를 열고 **실험적 기능(Experimental)** 탭으로 이동합니다.
2. **Custom Plugin Repositories**에 아래 주소를 추가하고 활성화한 뒤 저장합니다:
   ```text
   https://raw.githubusercontent.com/MWangLF/XcpCam/main/repo.json
   ```
3. `/xlplugins`에서 **XcpCam**을 검색해 설치합니다.
4. GPose 진입 시 자동으로 창이 열리며, `/xcpcam` 명령어로 언제든 열고 닫을 수 있습니다.

### 🛠️ 수동 설치 (개발 플러그인)

1. [Releases](https://github.com/MWangLF/XcpCam/releases)에서 최신 압축 파일을 내려받아 압축을 풉니다.
2. `/xlsettings` → **Experimental** → `Dev Plugin Locations`에 `XcpCam.dll`의 전체 경로를 추가합니다.
3. `/xlplugins` → **Dev Tools**에서 XcpCam을 활성화합니다.

## 지원 및 피드백

버그 제보나 기능 제안은 [GitHub Issues](https://github.com/MWangLF/XcpCam/issues)에 등록해 주세요.

## 소스에서 빌드

.NET 10 SDK와 Dalamud(API 15)가 필요합니다:

```bash
dotnet build -c Release
```

## 감사의 말

- [Brio](https://github.com/Etheirys/Brio) (GPL-3.0): XAT 카메라 재생 및 Vivacity 타임라인
- [XAT](https://github.com/Etheirys/XAT): `.xcp` 카메라 파일 포맷
- [Blender](https://github.com/blender/blender) (GPL-2.0-or-later): 커브 오브젝트, 베지어 핸들, NURBS, 경로 따라가기 / 추적 제약 설계
- [psenxiv/vista](https://github.com/psenxiv/vista): 호 길이 테이블 및 단조 PCHIP 타이밍 커브 아이디어
- [ReplayMod / ReplayStudio](https://github.com/ReplayMod/ReplayStudio): 키프레임 시간 기반 스플라인 카메라 편집 아이디어
- Shoemake (1985/1987): SQUAD 쿼터니언 보간

## 라이선스

본 프로젝트는 [GPL-3.0](LICENSE) 라이선스로 배포됩니다.
