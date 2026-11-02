H.264 regression fixtures
========================

idr.h264 and predicted.h264 are synthetic 16x16 Annex-B pictures generated
for these tests, not excerpts from third-party video. Both use neutral chroma (U=V=128); luma is 96 for the IDR and 160 for
the following P picture. The P picture deliberately omits SPS/PPS and depends
on the IDR, exercising continuous decoding and AVC444 main/aux reference reuse.

The tests also construct nonuniform YUV planes directly to check AVC444/v2
spatial reconstruction independently of these constant-colour pictures.
