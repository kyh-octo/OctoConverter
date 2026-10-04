# 🐙 OctoConverter

탭으로 장르를 나눈 범용 파일 변환기 (WPF / .NET 10). 무료이며 소스가 공개되어 있습니다.

## 다운로드

[Releases](../../releases) 페이지에서 `OctoConverter-Setup-<버전>.exe`를 받아 실행하면 됩니다.
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
- GIF·APNG·WebP·MP4·WebM 모두 인코딩한 파일의 실제 바이트 수가 목표보다 작을 때만 성공으로 처리합니다. 최대 재시도 후에도 목표를 넘으면 오류로 표시하고 불완전한 결과 파일을 남기지 않습니다.
- MP4·WebM은 실제 결과 크기에 따라 비디오 비트레이트를 다시 조정하며, 오디오에 배정하는 용량도 함께 계산합니다. 목표 크기에 맞추기 위해 영상 전체 길이는 유지합니다.
- 애니메이션 탭의 목표 용량 단위는 1 KB = 1,000 bytes, 1 MB = 1,000,000 bytes입니다. (이전 버전의 1,024배 단위와 다릅니다.)

애니메이션 탭에서는 프레임률(fps)을 1~240 사이 숫자로 직접 지정할 수 있으며 29.97 같은 소수 프레임률도 지원합니다. 너비는 2~16,384 픽셀 범위에서 직접 입력할 수 있습니다. 비율 축소는 원본 너비 대비 0% 초과, 100% 이하로 파일별 지정합니다.

## 공통 기능

- 파일·폴더 **드래그&드롭** + 다중 파일 일괄 변환 (이미지 병렬 4, FFmpeg 작업 병렬 2)
- 파일별 상태·진행률·결과 용량(증감률) 표시, 변환 중지(취소 시 불완전 출력 자동 삭제)
- 저장 위치: 원본 폴더 또는 지정 폴더, 파일명 충돌 시 자동 번호 부여
- 예상 용량: 이미지는 첫 파일 실제 인코딩 기반, 미디어는 비트레이트·해상도 기반 근사치

## FFmpeg

- 이미지·아이콘 탭은 FFmpeg 없이 바로 동작합니다.
- 동영상·음악·애니메이션 탭은 FFmpeg이 필요하며, 없으면 상단 배너의 **[FFmpeg 자동 설치]** 버튼으로
  Gyan full 정적 빌드의 GitHub 미러를 먼저 시도하고, 실패하면 BtbN win64 GPL, 마지막으로 gyan.dev full 빌드를 시도해 `%LocalAppData%\OctoConverter\ffmpeg`에 설치합니다. 연결 API는 15초, 다운로드 무응답은 30초 제한을 적용하며 진행률·전송 속도 표시와 취소를 지원합니다.
- ZIP에서 `ffmpeg.exe`와 `ffprobe.exe`를 함께 추출하고 두 파일을 실제 실행 검증한 뒤 설치합니다. 다운로드·압축·실행 검증에 실패하면 다음 후보를 시도하고, 설치가 실패하면 기존 파일을 복구합니다.
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

## 설치 파일 (Inno Setup)

[Inno Setup 6](https://jrsoftware.org/isinfo.php)이 필요합니다 (`winget install -e --id JRSoftware.InnoSetup`).

```powershell
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

1. self-contained 게시 (.NET 런타임 포함 → 대상 PC에 별도 설치 불필요)
2. Inno Setup 컴파일 → `installer\output\OctoConverter-Setup-<버전>.exe` (버전은 csproj의 `<Version>`)

관리자 권한 없이 사용자 단위로 설치되며(LocalAppData), 같은 AppId를 쓰므로 새 버전을 설치하면
이전 버전 위에 그대로 업그레이드됩니다. v1.0.x 시절의 WiX MSI 설치본이 남아 있으면 설치 전에 자동으로 제거합니다.

### 릴리즈 (원클릭)

`release.bat`을 실행하면 Git 최신 커밋 기준으로 설치 파일 빌드 → GitHub 릴리스(태그 `v<버전>`) 생성 → octo-brain.com 배포 갱신까지 자동으로 진행됩니다.
커밋되지 않은 로컬 변경은 릴리즈에 포함되지 않습니다. 옵션은 `installer\release.ps1` 머리말 참고.

## 회귀 검증

회귀 검증은 32개 체크로 구성되어 있으며, 별도 live-download 모드의 5개 체크는 실제 다운로드와 실행을 격리된 경로에서 확인합니다.

```powershell
dotnet run --project tests/OctoConverter.Regression
dotnet run --project tests/OctoConverter.Regression -- --live-download
```

live-download 검증은 격리 경로에서 수행한 결과이며, 일반 설치 절차의 실설치 테스트를 의미하지 않습니다.

## 라이선스

이 프로젝트는 [MIT 라이선스](LICENSE)로 배포됩니다.

동영상·음악·애니메이션 변환 시 앱이 Gyan 또는 [BtbN FFmpeg 빌드](https://github.com/BtbN/FFmpeg-Builds)(GPL)를
사용자 PC에 내려받아 **별도 프로그램으로** 실행합니다. FFmpeg은 이 저장소에 포함되지 않으며
FFmpeg 자체의 라이선스를 따릅니다. 문서 변환은 사용자 PC에 설치된 Microsoft Office 또는
LibreOffice를 사용합니다.
