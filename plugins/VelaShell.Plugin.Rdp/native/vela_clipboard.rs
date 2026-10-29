//! Bounded raw CLIPRDR transport for the managed text-only state machine.
//! All entry points must be serialized with connector/active-stage operations.
use std::collections::VecDeque;
use std::panic::{catch_unwind, AssertUnwindSafe};

use ironrdp::pdu::{gcc::ChannelName, PduResult};
use ironrdp::svc::{SvcClientProcessor, SvcMessage, SvcProcessor, SvcProcessorMessages};
use crate::connector::ffi::ClientConnector;
use crate::session::ffi::ActiveStage;
use crate::utils::ffi::VecU8;

const MAX_PACKET: usize = 2 * 1024 * 1024 + 8;
const MAX_QUEUE: usize = 12;

#[derive(Debug, Default)]
struct ManagedClipboard {
    incoming: VecDeque<Vec<u8>>,
}
ironrdp_core::impl_as_any!(ManagedClipboard);
impl SvcClientProcessor for ManagedClipboard {}
impl SvcProcessor for ManagedClipboard {
    fn channel_name(&self) -> ChannelName { ChannelName::from_static(b"cliprdr\0") }
    fn process(&mut self, payload: &[u8]) -> PduResult<Vec<SvcMessage>> {
        if payload.len() < 8 || payload.len() > MAX_PACKET || self.incoming.len() >= MAX_QUEUE {
            return Err(ironrdp::pdu::pdu_other_err!("managed clipboard budget exceeded"));
        }
        self.incoming.push_back(payload.to_vec());
        Ok(Vec::new())
    }
}
impl Drop for ManagedClipboard {
    fn drop(&mut self) {
        for bytes in &mut self.incoming { bytes.fill(0); }
    }
}

#[unsafe(no_mangle)]
pub(crate) extern "C" fn VelaClipboard_abi_version() -> u32 { 1 }

/// Drains asynchronous drive IRP completions on the session's owning pump.
#[unsafe(no_mangle)]
pub(crate) unsafe extern "C" fn VelaRdpdr_poll(stage: *mut ActiveStage) -> *mut VecU8 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(stage) = (unsafe { stage.as_mut() }) else { return std::ptr::null_mut(); };
        let Some(channel) = stage.0.get_svc_processor_mut::<ironrdp::rdpdr::Rdpdr>() else { return std::ptr::null_mut(); };
        let Ok(messages) = channel.poll_deferred_messages() else { return std::ptr::null_mut(); };
        if messages.is_empty() { return Box::into_raw(Box::new(VecU8(Vec::new()))); }
        match stage.0.process_svc_processor_messages(SvcProcessorMessages::<ironrdp::rdpdr::Rdpdr>::new(messages)) {
            Ok(frame) => Box::into_raw(Box::new(VecU8(frame))),
            Err(_) => std::ptr::null_mut(),
        }
    })).unwrap_or(std::ptr::null_mut())
}

/// The pointer must be a live connector allocated by this same native library.
#[unsafe(no_mangle)]
pub(crate) unsafe extern "C" fn VelaClipboard_attach(connector: *mut ClientConnector) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(wrapper) = (unsafe { connector.as_mut() }) else { return -1; };
        let Some(connector) = wrapper.0.as_mut() else { return -1; };
        if !matches!(connector.state, ironrdp::connector::ClientConnectorState::ConnectionInitiationSendRequest) { return -1; }
        if connector.get_static_channel_processor_mut::<ironrdp::cliprdr::CliprdrClient>().is_some()
            || connector.get_static_channel_processor_mut::<ManagedClipboard>().is_some() { return -1; }
        connector.attach_static_channel(ManagedClipboard::default());
        0
    })).unwrap_or(-1)
}

/// Returns one owned VecU8, or null if no packet is waiting. Use VecU8_destroy.
#[unsafe(no_mangle)]
pub(crate) unsafe extern "C" fn VelaClipboard_next(stage: *mut ActiveStage) -> *mut VecU8 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(stage) = (unsafe { stage.as_mut() }) else { return std::ptr::null_mut(); };
        let Some(channel) = stage.0.get_svc_processor_mut::<ManagedClipboard>() else { return std::ptr::null_mut(); };
        channel.incoming.pop_front().map(|bytes| Box::into_raw(Box::new(VecU8(bytes))))
            .unwrap_or(std::ptr::null_mut())
    })).unwrap_or(std::ptr::null_mut())
}

/// Encodes a single bounded CLIPRDR message with the negotiated channel framing.
/// Returns an owned VecU8 (including empty output), or null on error.
#[unsafe(no_mangle)]
pub(crate) unsafe extern "C" fn VelaClipboard_send(stage: *mut ActiveStage, bytes: *const u8, length: usize) -> *mut VecU8 {
    if bytes.is_null() || !(8..=MAX_PACKET).contains(&length) { return std::ptr::null_mut(); }
    catch_unwind(AssertUnwindSafe(|| {
        let Some(stage) = (unsafe { stage.as_mut() }) else { return std::ptr::null_mut(); };
        if stage.0.get_svc_processor_mut::<ManagedClipboard>().is_none() { return std::ptr::null_mut(); }
        let payload = unsafe { std::slice::from_raw_parts(bytes, length) }.to_vec();
        let messages = SvcProcessorMessages::<ManagedClipboard>::new(vec![SvcMessage::from(payload)]);
        match stage.0.process_svc_processor_messages(messages) {
            Ok(frame) => Box::into_raw(Box::new(VecU8(frame))),
            Err(_) => std::ptr::null_mut(),
        }
    })).unwrap_or(std::ptr::null_mut())
}
