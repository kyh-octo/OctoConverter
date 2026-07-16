# 🐙 OctoConverter

탭으로 장르를 나눈 범용 파일 변환기 (WPF / .NET 10). 무료이며 소스가 공개되어 있습니다.

## 다운로드

[Releases](../../releases) 페이지에서 `OctoConverterSetup-<버전>.exe`를 받아 실행하면 됩니다.
.NET 런타임이 내장되어 있어 별도 설치가 필요 없습니다. (Windows 10/11 x64)

## 탭 구성

| 탭 | 기능 | 엔진 |
|---|---|---|
| 🖼️ 이미지 | 출력: PNG·JPEG·WebP·**AVIF**·BMP·GIF·TIFF / 입력: 위 + HEIC·HEIF·TGA·DDS·JXR·JP2·ICO 등. 품질·크기 조절, **목표 용량 자동 품질 조절(JPEG·WebP·AVIF)**, 예상 용량 미리보기 | WIC + FFmpeg 보조 |
| 🎞️ 애니메이션 | GIF·APNG·WebP·MP4·**WebM** 상호 변환 (입력: WMV·FLV·TS·MTS·MPG 등 포함), fps·너비·색상 수·디더링·반복, GIF 팔레트 최적화, **목표 용량 자동 조절** | FFmpeg |
| ⭐ 아이콘 | 이미지(AVIF·HEIC 포함) → 다중 크기(16~256px) ICO 한 파일로 생성, 비율 유지 + 투명 패딩 | WIC + FFmpeg 보조 |
| 📄 문서 | Word(doc·docx·rtf·odt·txt)·Excel(xls·xlsx·csv·ods)·PowerPoint(ppt·pptx·odp)·이미지 → PDF. 이미지는 페이지 크기(원본/A4)·품질 설정과 **여러 장 → 한 PDF 합치기** 지원 | MS Office → LibreOffice 폴백, 이미지는 자체 PDF 엔진 |
| 🎵 음악 | 출력: MP3·M4A(AAC)·**Opus**·OGG·WAV·FLAC·**ALAC**·**AIFF** / 입력: 위 + APE·WV·AC3·DTS·MKA·AMR 및 대부분의 동영상(오디오 추출). **목표 용량 자동 비트레이트**, 음량 평준화(loudnorm) | FFmpeg |
| 🎬 동영상 | 출력: MP4(H.264/H.265/**AV1**)·MKV·**MOV**·WebM(VP9)·**AVI(Xvid)**·**WMV** + MP3·**M4A**·**WAV** 오디오 추출 / 입력: VOB·MTS·M2TS·MPG·3GP·ASF·RM 등 포함. 해상도·프레임, CRF/비트레이트/**목표 용량(2-pass)** | FFmpeg |

### 애니메이션 목표 용량 동작 방식
- **GIF·APNG**: 실제 인코딩 결과를 보고 폭 → 프레임 순으로 최대 6회 자동 축소
- **WebP**: 품질을 이진 탐색으로 자동 조절, 최저 품질로도 초과 시 크기 축소로 전환
- **MP4·WebM**: 목표 용량에서 비트레이트를 역산해 2-pass 인코딩

## 공통 기능

- 파일·폴더 **드래그&드롭** + 다중 파일 일괄 변환 (이미지 병렬 4, FFmpeg 작업 병렬 2)
- 파일별 상태·진행률·결과 용량(증감률) 표시, 변환 중지(취소 시 불완전 출력 자동 삭제)
- 저장 위치: 원본 폴더 또는 지정 폴더, 파일명 충돌 시 자동 번호 부여
- 예상 용량: 이미지는 첫 파일 실제 인코딩 기반, 미디어는 비트레이트·해상도 기반 근사치

## FFmpeg

- 이미지·아이콘 탭은 FFmpeg 없이 바로 동작합니다.
- 동영상·음악·애니메이션 탭은 FFmpeg이 필요하며, 없으면 상단 배너의 **[FFmpeg 자동 설치]** 버튼으로
  BtbN 공식 빌드(win64 GPL, 약 160MB)를 `%LocalAppData%\OctoConverter\ffmpeg`에 설치합니다.
- 직접 받은 `ffmpeg.exe`/`ffprobe.exe`를 프로그램 폴더에 넣거나 PATH에 두어도 인식합니다.

## 시작 속도

- 무거운 외부 라이브러리 없음(WPF 순정 + FFmpeg 외부 프로세스)
- 탭 화면은 처음 선택될 때 생성(지연 로딩), 미디어 분석은 파일 추가 시에만 수행
- 실측: 창 표시까지 약 0.1초
- 배포 시 추가 최적화: `dotnet publish -c Release -r win-x64 /p:PublishReadyToRun=true`

## 프로젝트 구조

```
OctoConverter/
├─ MainWindow.xaml(.cs)        # 탭 셸, FFmpeg 상태 배너, 지연 탭 로딩
├─ FFmpegDownloadWindow.xaml   # FFmpeg 자동 설치 대화상자
├─ Themes/Styles.xaml          # 색상·버튼·탭·진행률 바 등 공통 테마
├─ Models/FileItem.cs          # 변환 목록 항목(상태·진행률 바인딩)
├─ Services/
│  ├─ FFmpegService.cs         # 탐색/자동 설치/실행(진행률 파싱)
│  ├─ MediaProbe.cs            # ffprobe JSON 분석 + 캐시
│  ├─ ImageCodec.cs            # WIC 로드/리사이즈/인코딩/목표 용량 이진 탐색
│  ├─ IcoWriter.cs             # 다중 크기 ICO 바이너리 작성기
│  ├─ ConversionRunner.cs      # 병렬 일괄 변환 실행기, 출력 경로 규칙
│  └─ Formatters.cs            # 용량·시간 표기
├─ Controls/
│  ├─ FileListControl          # 드래그&드롭 파일 목록(공용)
│  └─ OutputLocationControl    # 저장 위치 선택(공용)
└─ Views/                      # ImageTab · AnimationTab · IconTab · MusicTab · VideoTab
```

빌드: Visual Studio 2022(17.12+)에서 `OctoConverter.slnx` 열기 또는 `dotnet build`.

## 설치 프로그램 (MSI)

`installer\build-installer.ps1`을 실행하면 두 단계가 자동으로 진행됩니다:

1. 자가 포함 단일 exe 게시 (.NET 런타임 포함 → 대상 PC에 별도 설치 불필요)
2. WiX로 MSI 빌드 → `installer\output\OctoConverterSetup-<버전>.msi` (약 49MB)

설치 시 `C:\Program Files\OctoConverter`에 설치되고 시작 메뉴·바탕화면 바로가기가 생성되며,
"앱 및 기능"에서 제거할 수 있습니다. 같은 UpgradeCode를 쓰므로 새 버전 MSI를 설치하면
이전 버전은 자동으로 교체됩니다 (버전은 csproj의 `<Version>`을 올리면 됨).

빌드 결과물은 두 가지입니다:
- `OctoConverterSetup-<버전>.exe` — MSI를 감싼 단일 설치 파일 (배포용 권장)
- `OctoConverterSetup-<버전>.msi` — 조용한 설치(`msiexec /i ... /qn`)나 사내 배포용

필요 도구:
```
dotnet tool install --global wix --version 5.0.2
wix extension add --global WixToolset.BootstrapperApplications.wixext/5.0.2
```
(WiX v7부터는 상용 조직에 OSMF 약관 동의가 필요하므로 v5 사용)

## 라이선스

이 프로젝트는 [MIT 라이선스](LICENSE)로 배포됩니다.

동영상·음악·애니메이션 변환 시 앱이 [BtbN FFmpeg 빌드](https://github.com/BtbN/FFmpeg-Builds)(GPL)를
사용자 PC에 내려받아 **별도 프로그램으로** 실행합니다. FFmpeg은 이 저장소에 포함되지 않으며
FFmpeg 자체의 라이선스를 따릅니다. 문서 변환은 사용자 PC에 설치된 Microsoft Office 또는
LibreOffice를 사용합니다.
