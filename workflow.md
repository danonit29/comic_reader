# Workflow & Quy Chuẩn Phát Triển - Comic Reader

> **Tài liệu chuẩn mực kỹ thuật và quy trình làm việc duy nhất cho repository `comic_reader_quoc_dang`.**
> Mọi AI Agent và lập trình viên BẮT BUỘC phải đọc kỹ toàn bộ quy tắc code, tiêu chuẩn giao diện và luồng xử lý tại file này trước khi tiến hành phân tích hoặc chỉnh sửa mã nguồn.

---

## 1. QUY TẮC NỀN TẢNG (CORE RULES)

1. **Ngôn ngữ phản hồi:** Luôn luôn trả lời bằng **tiếng Việt**.
2. **Kỷ luật giao thức (Protocol Discipline):**
   - Bắt buộc áp dụng skill `teamwork-preview-protocol` và `prompt-optimizer` cho mọi phiên làm việc, bất kể người dùng có tag tên skill hay không.
   - Luôn luôn xuất **Teamwork Preview** nhỏ gọn trước khi thực hiện chỉnh sửa lớn và kiểm chứng sau khi sửa.
3. **Phạm vi nền tảng (Strict Multi-Platform Scope):**
   - Ứng dụng được thiết kế và cấu hình cho **DUY NHẤT 2 NỀN TẢNG: Windows và Android**.
   - **TUYỆT ĐỐI KHÔNG ĐƯỢC THÊM BẤT KỲ HỆ ĐIỀU HÀNH NÀO KHÁC** (Không thêm iOS, macOS, Linux, Web/Wasm).
4. **Chuẩn chất lượng build:**
   - Sau khi code hoặc sửa bug, bắt buộc chạy build kiểm thử dứt điểm đạt **`0 Error, 0 Warning`**.
5. **Kỷ luật Git & Commit:**
   - Chỉ được phép **commit local**, đưa mã hash ngắn cho người dùng.
   - Tuyệt đối **không tự ý git push lên GitHub trừ khi người dùng có yêu cầu rõ ràng**.
   - Định dạng commit theo Conventional Commits: `<type>(<scope>): <mô tả ngắn gọn>` ($\le 72$ ký tự).
6. **Định dạng 3 dòng trạng thái chuẩn bắt buộc ở cuối mỗi câu trả lời:**
   ```text
   commit local: <mã hash ngắn>
   commit github: <"không" hoặc "có" nếu được yêu cầu push>
   path exe: <đường dẫn file .exe đã build>
   ```

---

## 2. KIẾN TRÚC HỆ THỐNG (SYSTEM ARCHITECTURE)

### 2.1. Phân chia Project (Solution Structure)

```text
comic reader quoc dang/
├── ComicReader.slnx                    # File Solution chính kết nối 3 project
├── build.bat                           # Script 1-click build toàn bộ Windows EXE & Android APK
├── workflow.md                         # Quy chuẩn kỹ thuật & quy trình (SSOT)
├── README.md                           # Giới thiệu tổng quan
│
├── ComicReader/                        # Core Shared Library (Class Library .NET 8)
│   ├── App.axaml & App.axaml.cs        # Entry logic Avalonia, DI container, Platform lifetime router
│   ├── Views/                          # XAML Views & Controls
│   │   ├── MainWindow.axaml            # Shell Window riêng cho Desktop (Window chrome, titlebar)
│   │   ├── MainView.axaml              # Cross-Platform UserControl (giao diện chính dùng chung)
│   │   ├── ReaderView.axaml            # Trình đọc truyện tích hợp (cuộn ảnh, zoom, phím tắt)
│   │   └── RenameDialog.axaml          # Modal đổi tên truyện/folder
│   ├── ViewModels/                     # MVVM ViewModels (CommunityToolkit.Mvvm)
│   │   ├── MainViewModel.cs            # Điều phối thư viện, điều hướng, tìm kiếm, đọc truyện
│   │   ├── FolderViewModel.cs          # Quản lý danh mục folder, sắp xếp A-Z
│   │   └── ReaderViewModel.cs          # Logic đọc truyện, render trang, bộ nhớ đệm
│   ├── Services/                       # Tầng dịch vụ & Interfaces
│   │   ├── Interfaces/                 # IComicReaderService, IFileService, IStorageService, ...
│   │   ├── FileService.cs              # Quản lý đọc file/folder qua StorageProvider (SAF trên Android)
│   │   ├── PdfService.cs               # Trình kết xuất file PDF (Docnet.Core / PDFium)
│   │   ├── ZipService.cs               # Trình giải nén/đọc ảnh từ file .zip / .cbz
│   │   ├── StorageService.cs           # Lưu trữ JSON tiến độ đọc và cấu hình ứng dụng
│   │   └── BookmarkService.cs          # Quản lý đánh dấu trang truyện
│   ├── Models/                         # Data Models (ComicBook, ComicFolder, AppSettings, ...)
│   ├── Helpers/                        # Tiện ích, Converters, ImageCacheManager, AppConstants
│   └── Styles/                         # Theme, màu sắc, Icon Path, Style mẫu Avalonia
│
├── ComicReader.Desktop/                # Đầu chạy Windows Desktop (WinExe .NET 8)
│   ├── ComicReader.Desktop.csproj      # Reference ComicReader + Avalonia.Desktop
│   ├── Program.cs                      # Main entry point gọi StartWithClassicDesktopLifetime
│   └── app.manifest                    # Windows dpiAware & long path manifest
│
└── ComicReader.Android/                # Đầu chạy Android (Exe net10.0-android)
    ├── ComicReader.Android.csproj      # Reference ComicReader + Avalonia.Android + SplashScreen
    ├── MainActivity.cs                 # AvaloniaMainActivity<App> cho Android
    ├── Properties/AndroidManifest.xml  # Quyền hạn lưu trữ, ApplicationId: com.ghostminhtoan.comicreader
    └── Resources/                      # Resource Android (drawable, values/styles, colors)
```

---

## 3. TIÊU CHUẨN CODE VÀ GIAO DIỆN (UI & CODING STANDARDS)

### 3.1. Phân luồng Lifetime Avalonia
- **Trên Desktop (Windows):** `ApplicationLifetime` là `IClassicDesktopStyleApplicationLifetime`. Khởi tạo `MainWindow` (kế thừa `Window`) có title bar tùy biến (Minimize, Maximize, Close) và nhúng `MainView`.
- **Trên Mobile (Android):** `ApplicationLifetime` là `ISingleViewApplicationLifetime`. Gán trực tiếp `MainView` (kế thừa `UserControl`), loại bỏ các nút điều khiển cửa sổ kiểu Windows.

### 3.2. Truy cập hệ thống tập tin (File System & StorageProvider)
- Sử dụng abstraction `IStorageService` và `IFileService`.
- Khi mở file/folder picker, bắt buộc lấy `StorageProvider` qua `TopLevel.GetTopLevel(...)`:
  - Desktop: lấy từ `desktop.MainWindow`.
  - Android: lấy từ `singleView.MainView`.
- Đường dẫn lưu trữ dữ liệu (library data, settings, covers) sử dụng `Environment.SpecialFolder.LocalApplicationData` để đảm bảo tương thích 100% với Android sandboxed storage mà không vi phạm quyền hạn.

### 3.3. Thread Safety & Asynchronous Programming
- Mọi thao tác cập nhật UI từ luồng nền bắt buộc điều phối qua `Dispatcher.UIThread.InvokeAsync(...)` hoặc `Post(...)`.
- Giữ vững quy tắc `async/await`, tôn trọng `CancellationToken`. Không dùng `.Result` hoặc `.Wait()` gây deadlock trên luồng giao diện.
- Render ảnh/PDF chạy trên `Task.Run` hoặc background thread để không gây khựng UI (Zero-jank scrolling).

---

## 4. HƯỚNG DẪN BUILD & XUẤT XƯỞNG (BUILD & PUBLISH GUIDE)

### 4.1. Lệnh 1-Click: `build.bat`
Chạy file `build.bat` tại thư mục gốc để tự động thực hiện:
1. Dọn dẹp các thư mục artifacts cũ và tắt tiến trình đang chạy.
2. Xuất bản **Windows x64 Single-file Standalone EXE** (`publish\ComicReader.exe`).
3. Biên dịch và đóng gói **Android Release Signed APK** (`publish\ComicReader-Signed.apk`).
4. Tổng kết kích thước và đường dẫn các file xuất bản tại thư mục `publish/`.

### 4.2. Lệnh CLI thủ công
- **Build toàn bộ Solution:**
  ```cmd
  dotnet build ComicReader.slnx -c Release
  ```
- **Build riêng Windows Desktop:**
  ```cmd
  dotnet publish ComicReader.Desktop\ComicReader.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
  ```
- **Build riêng Android APK:**
  ```cmd
  dotnet build ComicReader.Android\ComicReader.Android.csproj -c Release
  ```

---

## 5. BẢO VỆ CHỐNG REGRESSION (REGRESSION DEFENSE)
- Trước khi thêm thư viện hoặc API mới, kiểm tra tính tương thích trên cả 2 nền tảng Windows và Android.
- Không đưa vào các API đặc thù của Windows (như `Win32`, `P/Invoke` Windows DLL) vào project lõi `ComicReader`.
- Đảm bảo `Docnet.Core` hoặc các thành phần native có cơ chế bắt lỗi an toàn (graceful fallback) khi chạy trên kiến trúc thiết bị không hỗ trợ.
