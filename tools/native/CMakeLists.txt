cmake_minimum_required(VERSION 3.20)
project(ReportDiffOpenCvSharpRuntime LANGUAGES CXX)

# 比較に使うモジュールだけをリンクし、上流の不要な検出処理を避ける。
find_package(OpenCV 4.13 REQUIRED COMPONENTS core imgproc imgcodecs)
file(GLOB wrapper_sources CONFIGURE_DEPENDS "${OPENCVSHARP_SOURCE}/src/OpenCvSharpExtern/*.cpp")
add_library(OpenCvSharpExtern SHARED ${wrapper_sources})
target_compile_features(OpenCvSharpExtern PRIVATE cxx_std_11)
target_include_directories(OpenCvSharpExtern PRIVATE ${OpenCV_INCLUDE_DIRS})
target_compile_definitions(OpenCvSharpExtern PRIVATE
    NO_CONTRIB NO_STITCHING NO_CALIB3D NO_VIDEO NO_FEATURES2D NO_FLANN
    NO_DNN NO_ML NO_OBJDETECT NO_PHOTO NO_BARCODE NO_HIGHGUI NO_VIDEOIO NO_ICONV)
target_link_libraries(OpenCvSharpExtern PRIVATE ${OpenCV_LIBS})
set_target_properties(OpenCvSharpExtern PROPERTIES
    CXX_VISIBILITY_PRESET hidden VISIBILITY_INLINES_HIDDEN YES)
